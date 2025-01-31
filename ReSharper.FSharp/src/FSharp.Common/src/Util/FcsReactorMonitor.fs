namespace JetBrains.ReSharper.Plugins.FSharp

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open FSharp.Compiler.SourceCodeServices
open JetBrains.Application
open JetBrains.Application.Environment
open JetBrains.Application.Environment.Helpers
open JetBrains.Application.Threading
open JetBrains.DataFlow
open JetBrains.Diagnostics
open JetBrains.Lifetimes
open JetBrains.Platform.RdFramework.Util
open JetBrains.ProjectModel
open JetBrains.RdBackend.Common.Features.BackgroundTasks
open JetBrains.ReSharper.Plugins.FSharp.Settings
open JetBrains.Rider.Backend.Features.BackgroundTasks
open JetBrains.Util

type IMonitoredReactorOperation =
    inherit IDisposable
    abstract OperationName : string

[<RequireQualifiedAccess>]
module MonitoredReactorOperation =
    let empty opName =
        { new IMonitoredReactorOperation with
            override _.Dispose () = ()
            override _.OperationName = opName
        }


type IFcsReactorMonitor =
    inherit IReactorListener

    /// How long after the reactor becoming busy that the background task should be shown
    abstract FcsBusyDelay : IProperty<TimeSpan>

    abstract MonitorOperation : opName: string -> IMonitoredReactorOperation

[<ShellComponent>]
type FcsReactorMonitor(lifetime: Lifetime, backgroundTaskHost: RiderBackgroundTaskHost, threading: IThreading,
        logger: ILogger, configurations: RunsProducts.ProductConfigurations) as this =

    let isInternalMode = configurations.IsInternalMode()

    /// How long after the reactor becoming busy that the background task should be shown
    let showDelay = new Property<TimeSpan>("showDelay")

    let mutable lastOperationId = 0L
    let operations = ConcurrentDictionary<int64, StackTrace>()

    /// How long after the reactor becoming free that the background task should be hidden
    let hideDelay = TimeSpan.FromSeconds 0.5

    /// How long an operation must be running for before a stack trace of the enqueuing thread is dumped
    let dumpStackDelay = TimeSpan.FromSeconds 10.0

    let mutable slowOperationTimer = null
    let isReactorBusy = new Property<bool>("isReactorBusy")

    let taskHeader = new Property<string>("taskHeader")
    let taskDescription = new Property<string>("taskDescription")
    let showBackgroundTask = new Property<bool>("showBackgroundTask")

    let createNewTask (activeLifetime: Lifetime) =
        // Only show the background task after we've been busy for some time
        threading.QueueAt(activeLifetime, "FcsReactorMonitor.AddNewTask", showDelay.Value, fun () ->
            let settingsProvider = this.SettingsProvider
            if isNotNull settingsProvider && not settingsProvider.EnableReactorMonitor then () else

            let task =
                RiderBackgroundTaskBuilder.Create()
                    .WithTitle("F# Compiler Service is busy")
                    .WithHeader(taskHeader)
                    .WithDescription(taskDescription)
                    .AsIndeterminate()
                    .AsNonCancelable()
                    .Build()

            backgroundTaskHost.AddNewTask(activeLifetime, task))

    /// Called when a FCS operation starts.
    /// Always called from the current FCS reactor thread.
    let onOperationStart (opName: string) (opArg: string) =
        // Try to parse the operation ID from the front of the opName
        let operationId, opName =
            let endIndex = opName.IndexOf '}'
            if endIndex = -1 || opName.[0] <> '{' then None, opName else

            match Int64.TryParse(opName.Substring(1, endIndex - 1)) with
            | false, _ -> None, opName
            | true, operationId ->

            let opName = opName.Substring(endIndex + 1)

            match operations.TryGetValue operationId with
            | null -> None, opName
            | stackTrace ->

            // Warn if the operation still hasn't finished in a few seconds
            slowOperationTimer <-
                let callback _ = logger.Warn("Operation '{0}' ({1}) is taking a long time. Stack trace:\n{2}", opName, opArg, stackTrace)
                new Timer(callback, null, dumpStackDelay, Timeout.InfiniteTimeSpan)

            Some operationId, opName

        if isInternalMode then sprintf "Processing F# / %s" opName
        else "Processing F#"
        |> taskHeader.SetValue
        |> ignore

        let opArg = if Path.IsPathRooted opArg then Path.GetFileName opArg else opArg
        match operationId with
        | None -> taskDescription.SetValue(opArg) |> ignore
        | Some operationId -> taskDescription.SetValue(sprintf "%s (operation #%d)" opArg operationId) |> ignore

        isReactorBusy.SetValue true |> ignore

    /// Called when a FCS operation ends.
    /// Always called from the current FCS reactor thread.
    let onOperationEnd () =
        // Cancel slow operation timer - the operation is now finished
        if isNotNull slowOperationTimer then
            slowOperationTimer.Dispose()
            slowOperationTimer <- null

        isReactorBusy.SetValue false |> ignore

    do
        showBackgroundTask.WhenTrue(lifetime, Action<_> createNewTask)

        isReactorBusy.WhenTrue(lifetime, fun lt -> showBackgroundTask.SetValue true |> ignore)

        isReactorBusy.WhenFalse(lifetime, fun lt ->
            threading.QueueAt(lt, "FcsReactorMonitor.HideTask", hideDelay, fun () ->
                showBackgroundTask.SetValue false |> ignore))

    member val SettingsProvider: FcsReactorMonitorSettingsProvider = null with get, set

    interface IFcsReactorMonitor with
        override _.FcsBusyDelay = showDelay :> _

        override _.MonitorOperation opName =
            // Only monitor operations when trace logging is enabled
            if not (logger.IsEnabled LoggingLevel.TRACE) then
                MonitoredReactorOperation.empty opName
            else

            let stackTrace = StackTrace(1, true)
            let operationId = Interlocked.Increment &lastOperationId
            operations.TryAdd(operationId, stackTrace) |> ignore

            { new IMonitoredReactorOperation with
                override _.Dispose () = match operations.TryRemove operationId with _ -> ()
                override _.OperationName = sprintf "{%d}%s" operationId opName
            }

    interface IReactorListener with
        override _.OnReactorPauseBeforeBackgroundWork pauseMillis =
            logger.Trace("Pausing before background work for {0:0.}ms", pauseMillis)
        override _.OnReactorOperationStart userOpName opName opArg approxQueueLength =
            logger.Verbose("--> {0}.{1} ({2}), queue length {3}", userOpName, opName, opArg, approxQueueLength)
            onOperationStart (userOpName + "." + opName) opArg
        override _.OnReactorOperationEnd userOpName opName _opArg elapsed =
            let level =
                if elapsed > showDelay.Value then LoggingLevel.WARN
                else LoggingLevel.VERBOSE
            logger.LogMessage(level, "<-- {0}.{1}, took {2:0.}ms", userOpName, opName, elapsed.TotalMilliseconds)
            onOperationEnd ()
        override _.OnReactorBackgroundStart bgUserOpName bgOpName bgOpArg =
            // todo: do we want to show background steps too?
            logger.Trace("--> Background step {0}.{1} ({2})", bgUserOpName, bgOpName, bgOpArg)
        override _.OnReactorBackgroundCancelled bgUserOpName bgOpName _bgOpArg =
            logger.Trace("<-- Background step {0}.{1}, was cancelled", bgUserOpName, bgOpName)
        override _.OnReactorBackgroundEnd _bgUserOpName _bgOpName _bgOpArg elapsed =
            let level =
                if elapsed > showDelay.Value then LoggingLevel.WARN
                else LoggingLevel.TRACE
            logger.LogMessage(level, "<-- Background step took {0:0.}ms", elapsed.TotalMilliseconds)
        override _.OnSetBackgroundOp approxQueueLength =
            logger.Trace("Enqueue start background, queue length {0}", approxQueueLength)
        override _.OnCancelBackgroundOp () =
            logger.Trace("Trying to cancel any active background work...")
        override _.OnEnqueueOp userOpName opName opArg approxQueueLength =
            logger.Trace("Enqueue: {0}.{1} ({2}), queue length {3}", userOpName, opName, opArg, approxQueueLength)

and
    [<SolutionComponent; AllowNullLiteral>]
    FcsReactorMonitorSettingsProvider(lifetime, solution, monitor: IFcsReactorMonitor, settings,
        settingsSchema) as this =
    inherit FSharpSettingsProviderBase<FSharpOptions>(lifetime, solution, settings, settingsSchema)

    let isEnabledProperty = base.GetValueProperty<bool>("EnableReactorMonitor")

    do
        match monitor with
        | :? FcsReactorMonitor as fcsReactorMonitor ->
            fcsReactorMonitor.SettingsProvider <- this
            lifetime.OnTermination(fun _ -> fcsReactorMonitor.SettingsProvider <- null) |> ignore
        | _ -> ()

    member x.EnableReactorMonitor = isEnabledProperty.Value


[<SolutionComponent>]
type ReactorMonitorSolutionLink(lifetime: Lifetime, solution: ISolution, reactorMonitor: IFcsReactorMonitor) =
    do
        match solution.RdFSharpModel() with
        | null -> ()
        | model ->
            model.FcsBusyDelayMs.FlowInto(lifetime, reactorMonitor.FcsBusyDelay,
                fun ms -> TimeSpan.FromMilliseconds(float ms))


type FcsReactorMonitorStub() =
    let fcsShowDelay = new Property<TimeSpan>("fcsShowDelay")

    interface IFcsReactorMonitor with
        member x.FcsBusyDelay = fcsShowDelay :> _
        member x.MonitorOperation opName = MonitoredReactorOperation.empty opName

    interface IReactorListener with
        override _.OnReactorPauseBeforeBackgroundWork _ = ()
        override _.OnReactorOperationStart _ _ _ _ = ()
        override _.OnReactorOperationEnd _ _ _ _ = ()
        override _.OnReactorBackgroundStart _ _ _ = ()
        override _.OnReactorBackgroundCancelled _ _ _ = ()
        override _.OnReactorBackgroundEnd _ _ _ _ = ()
        override _.OnSetBackgroundOp _ = ()
        override _.OnCancelBackgroundOp () = ()
        override _.OnEnqueueOp _ _ _ _ = ()

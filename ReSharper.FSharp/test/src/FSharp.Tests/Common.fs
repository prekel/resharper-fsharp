namespace JetBrains.ReSharper.Plugins.FSharp.Tests

open System
open System.Collections.Generic
open System.Threading
open FSharp.Compiler.CodeAnalysis
open JetBrains.Application.Components
open JetBrains.Application.platforms
open JetBrains.DataFlow
open JetBrains.Diagnostics
open JetBrains.Lifetimes
open JetBrains.ProjectModel
open JetBrains.ProjectModel.Properties.Managed
open JetBrains.ReSharper.Plugins.FSharp
open JetBrains.ReSharper.Plugins.FSharp.Checker
open JetBrains.ReSharper.Plugins.FSharp.ProjectModel
open JetBrains.ReSharper.Plugins.FSharp.Psi
open JetBrains.ReSharper.Plugins.FSharp.Psi.Resolve
open JetBrains.ReSharper.Psi
open JetBrains.ReSharper.Psi.Modules
open JetBrains.ReSharper.Psi.Util
open JetBrains.ReSharper.TestFramework
open JetBrains.TestFramework
open JetBrains.TestFramework.Projects
open JetBrains.Util.Dotnet.TargetFrameworkIds
open Moq
open NUnit.Framework

[<assembly: Apartment(ApartmentState.STA)>]
do()


module FSharpTestAttribute =
    let extensions =
        [| FSharpProjectFileType.FsExtension
           FSharpSignatureProjectFileType.FsiExtension |]
        |> HashSet

    let targetFrameworkId =
        TargetFrameworkId.Create(FrameworkIdentifier.NetFramework, Version(4, 5, 1), ProfileIdentifier.Default)


[<AutoOpen>]
module PackageReferences =
    let [<Literal>] FSharpCorePackage = "FSharp.Core/4.7.2"


type FSharpTestAttribute(extension) =
    inherit TestPackagesAttribute()

    member val ReferenceFSharpCore = true with get, set

    new () =
        FSharpTestAttribute(FSharpProjectFileType.FsExtension)

    interface ITestTargetFrameworkIdProvider with
        member x.GetTargetFrameworkId() = FSharpTestAttribute.targetFrameworkId
        member this.Inherits = false

    interface ITestMsCorLibFlagProvider with
        member this.GetMsCorLibFlag() = ReferenceDlls.MsCorLib

    interface ITestFileExtensionProvider with
        member x.Extension = extension

    interface ITestProjectPropertiesProvider with
        member x.GetProjectProperties(targetFrameworkIds, _) =
            FSharpProjectPropertiesFactory.CreateProjectProperties(targetFrameworkIds)

    interface ITestProjectOutputTypeProvider with
        member x.Priority = -1
        member x.ProjectOutputType = ProjectOutputType.CONSOLE_EXE

    interface ITestProjectFilePropertiesProvider with
        member x.Process(path, properties, projectDescriptor) =
            if FSharpTestAttribute.extensions.Contains(path.ExtensionWithDot) then
                for targetFrameworkId in projectDescriptor.ProjectProperties.ActiveConfigurations.TargetFrameworkIds do
                    properties.SetBuildAction(BuildAction.COMPILE, targetFrameworkId)

    override this.GetPackages _ =
        [| if this.ReferenceFSharpCore then
               TestPackagesAttribute.ParsePackageName(FSharpCorePackage) |] :> _

type FSharpSignatureTestAttribute() =
    inherit FSharpTestAttribute(FSharpSignatureProjectFileType.FsiExtension)


type FSharpScriptTestAttribute() =
    inherit FSharpTestAttribute(FSharpScriptProjectFileType.FsxExtension)


[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Class, Inherited = false)>]
type FSharpLanguageLevelAttribute(languageLevel: FSharpLanguageLevel) =
    inherit TestAspectAttribute()

    override this.OnBeforeTestExecute(context) =
        let project = context.TestProject

        let property = project.GetSolution().GetComponent<FSharpLanguageLevelProjectProperty>()
        property.OverrideLanguageLevel(context.TestLifetime, languageLevel, project)

        PsiFileCachedDataUtil.InvalidateInAllProjectFiles(project, FSharpLanguage.Instance, FSharpLanguageLevel.key)


[<SolutionComponent>]
type TestFSharpResolvedSymbolsCache(lifetime, checkerService, psiModules, fcsProjectProvider, assemblyReaderShim) =
    inherit FcsResolvedSymbolsCache(lifetime, checkerService, psiModules, fcsProjectProvider, assemblyReaderShim)

    override x.Invalidate _ =
        x.PsiModulesCaches.Clear()

    interface IHideImplementation<FcsResolvedSymbolsCache>


[<SolutionComponent>]
type TestFcsProjectBuilder(checkerService: FcsCheckerService, modulePathProvider: ModulePathProvider, logger: ILogger) =
    inherit FcsProjectBuilder(checkerService, Mock<_>().Object, logger, modulePathProvider)

    override x.GetProjectItemsPaths(project, targetFrameworkId) =
        project.GetAllProjectFiles()
        |> Seq.filter (fun file -> file.LanguageType.Is<FSharpProjectFileType>())
        |> Seq.map (fun file -> file.Location, file.Properties.GetBuildAction(targetFrameworkId))
        |> Seq.toArray

    interface IHideImplementation<FcsProjectBuilder>


[<SolutionComponent>]
type TestFcsProjectProvider(lifetime: Lifetime, checkerService: FcsCheckerService,
        fcsProjectBuilder: FcsProjectBuilder, scriptFcsProjectProvider: IScriptFcsProjectProvider) as this =
    do
        checkerService.FcsProjectProvider <- this
        lifetime.OnTermination(fun _ -> checkerService.FcsProjectProvider <- Unchecked.defaultof<_>) |> ignore

    let getFcsProject (psiModule: IPsiModule) =
        fcsProjectBuilder.BuildFcsProject(psiModule, psiModule.ContainingProjectModule.As<IProject>())

    let getProjectOptions (sourceFile: IPsiSourceFile) =
        let fcsProject = getFcsProject sourceFile.PsiModule
        Some fcsProject.ProjectOptions

    interface IHideImplementation<FcsProjectProvider>

    interface IFcsProjectProvider with
        member x.HasPairFile _ = false

        member x.GetProjectOptions(sourceFile: IPsiSourceFile) =
            if sourceFile.LanguageType.Is<FSharpScriptProjectFileType>() then
                scriptFcsProjectProvider.GetScriptOptions(sourceFile) else

            getProjectOptions sourceFile

        member x.GetParsingOptions(sourceFile) =
            if isNull sourceFile then sandboxParsingOptions else

            let isScript = sourceFile.LanguageType.Is<FSharpScriptProjectFileType>()
            let targetFrameworkId = sourceFile.PsiModule.TargetFrameworkId

            let isExe =
                match sourceFile.GetProject() with
                | null -> false
                | project ->

                match project.ProjectProperties.ActiveConfigurations.TryGetConfiguration(targetFrameworkId) with
                | :? IManagedProjectConfiguration as cfg ->
                    cfg.OutputType = ProjectOutputType.CONSOLE_EXE
                | _ -> false

            let project = sourceFile.GetProject().NotNull()
            let paths = fcsProjectBuilder.GetProjectItemsPaths(project, targetFrameworkId) |> Array.map (fst >> string)

            { FSharpParsingOptions.Default with
                SourceFiles = paths
                IsExe = isExe
                IsInteractive = isScript }

        member x.GetFileIndex(sourceFile) =
            if sourceFile.LanguageType.Is<FSharpScriptProjectFileType>() then 0 else

            let fcsProject = getFcsProject sourceFile.PsiModule
            match fcsProject.FileIndices.TryGetValue(sourceFile.GetLocation()) with
            | true, index -> index
            | _ -> -1

        member x.InvalidateDirty() = ()
        member x.ModuleInvalidated = new Signal<_>("Todo") :> _

        member x.InvalidateReferencesToProject _ = false
        member x.HasFcsProjects = false
        member this.GetProjectOptions(_: IPsiModule): FSharpProjectOptions option = failwith "todo"

        member this.GetFcsProject(psiModule) = Some (getFcsProject psiModule)

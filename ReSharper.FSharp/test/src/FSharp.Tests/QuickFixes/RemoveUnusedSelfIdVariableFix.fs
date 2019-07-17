namespace JetBrains.ReSharper.Plugins.FSharp.Tests.Features

open JetBrains.ReSharper.FeaturesTestFramework.Intentions
open JetBrains.ReSharper.Plugins.FSharp.Psi.Features.Daemon.QuickFixes
open JetBrains.ReSharper.Plugins.FSharp.Tests.Common
open NUnit.Framework

[<FSharpTest>]
type RemoveUnusedSelfIdVariableTest() =
    inherit QuickFixTestBase<RemoveUnusedSelfIdVariableFix>()

    override x.RelativeTestDataPath = "features/quickFixes/removeUnusedSelfIdVariable"

    [<Test>] member x.``Space 01 - Simple``() = x.DoNamedTest()
    [<Test>] member x.``Space 02 - Normalize``() = x.DoNamedTest()
    [<Test>] member x.``Space 03 - Add space``() = x.DoNamedTest()
    [<Test>] member x.``Space 04 - Add space 2``() = x.DoNamedTest()

    [<Test>] member x.``Comments 01``() = x.DoNamedTest()

    [<Test>] member x.``Member ctor 01``() = x.DoNamedTest()

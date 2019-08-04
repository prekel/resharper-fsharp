[<AutoOpen; Extension>]
module JetBrains.ReSharper.Plugins.FSharp.Util.FSharpAssemblyUtil

open JetBrains.Diagnostics
open JetBrains.Metadata.Reader.API
open JetBrains.ProjectModel
open JetBrains.ReSharper.Psi
open JetBrains.ReSharper.Psi.Modules
open JetBrains.ReSharper.Resources.Shell
open JetBrains.Util

[<CompiledName("InterfaceDataVersionAttrTypeName")>]
let interfaceDataVersionAttrTypeName = clrTypeName "Microsoft.FSharp.Core.FSharpInterfaceDataVersionAttribute"

let isFSharpAssemblyKey = Key("IsFSharpAssembly")

[<Extension; CompiledName("IsFSharpAssembly")>]
let isFSharpAssembly (psiModule: IPsiModule) =
    match psiModule.ContainingProjectModule with
    | :? IProject -> false
    | _ ->

    match psiModule.GetData(isFSharpAssemblyKey) with
    | null ->
        use cookie = ReadLockCookie.Create()
        let attrs = psiModule.GetPsiServices().Symbols.GetModuleAttributes(psiModule)
        let isFSharpAssembly = attrs.HasAttributeInstance(interfaceDataVersionAttrTypeName, false)

        psiModule.PutData(isFSharpAssemblyKey, if isFSharpAssembly then BooleanBoxes.True else BooleanBoxes.False)
        isFSharpAssembly

    | value -> value == BooleanBoxes.True

let [<Literal>] signatureInfoResourceName = "FSharpSignatureInfo."
let [<Literal>] signatureInfoResourceNameOld = "FSharpSignatureData."

let isSignatureDataResource (manifestResource: IMetadataManifestResource) (compilationUnitName: outref<string>) =
    let name = manifestResource.Name
    if startsWith signatureInfoResourceName name then
        compilationUnitName <- name.Substring(signatureInfoResourceName.Length)
        true
    elif startsWith signatureInfoResourceNameOld name then
        compilationUnitName <- name.Substring(signatureInfoResourceNameOld.Length)
        true
    else false

let getFSharpSignatureInfos (psiModule: IPsiModule) =
    match psiModule.As<IAssemblyPsiModule>() with
    | null -> null
    | assemblyPsiModule ->

    let path = assemblyPsiModule.Assembly.Location
    if isNull path then null else

    Assertion.Assert(isFSharpAssembly psiModule, "isFSharpAssembly psiModule")

    let metadataLoader = new MetadataLoader()
    let metadataAssembly = metadataLoader.TryLoadFrom(path, JetFunc<_>.False)
    if isNull metadataAssembly then null else

    let resources = metadataAssembly.GetManifestResources()

    for manifestResource in resources do
        let mutable compilationUnitName = Unchecked.defaultof<_>
        if not (isSignatureDataResource manifestResource &compilationUnitName) then () else

        use stream = manifestResource.GetDisposition().CreateResourceReader()
        // todo: add metadata reading
        ()

    null
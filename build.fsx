#r "paket:
nuget FSharp.Core
nuget Fake.Core.ReleaseNotes
nuget Fake.Core.Process
nuget Fake.IO.FileSystem
nuget Fake.IO.Zip
nuget Fake.BuildServer.TeamFoundation
nuget Fake.Core.Target
nuget Fake.DotNet.Cli
nuget Fake.Core.Environment
//"
#load ".fake/build.fsx/intellisense.fsx"
#load "build-utils.fsx"
open ``Build-utils``

open System
open System.Text
open System.Text.RegularExpressions
open System.IO
open System.Collections.Generic
open Fake.BuildServer
open Fake.Core
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.DotNet


open System.Net.Http
open System.Collections.Generic

BuildServer.install [
    TeamFoundation.Installer
]

let vault =
    match Vault.fromFakeEnvironmentOrNone() with
    | Some v -> v
    | None -> TeamFoundation.variables
let getVarOrDefault name def =
    match vault.TryGet name with
    | Some v -> v
    | None -> Environment.environVarOrDefault name def
let publisher = getVarOrDefault "PUBLISHER" "IsaacAbraham"
let release = ReleaseNotes.load "RELEASE_NOTES.md"

let artifactsDir = getVarOrDefault "artifactsdirectory" "publish"
let version = release.NugetVersion


open Fake.Core
open Fake.Core.TargetOperators

let extensionDirNames = 
    [ "PaketCredentialCleanup";"SetPaketCredentialProvider";
      "FSharpScript"; "FAKE4Runner"; "PaketRestore"; "FAKE5"; "FAKE5Vault" ]

let dirs =
    extensionDirNames
    |> List.map (fun dir -> "Tasks" </> dir) 

let asDevel d = 
    let devDir = d + ".dev"
    if Directory.Exists devDir then devDir else d

do Npm.command "." "version" []

Target.create "Clean" (fun _ ->
    Shell.cleanDir "_build"
)

let npmCi dir =
    try Npm.ci dir []
    with _ ->
        printfn "npm ci failed, trying to delete the lockfile"
        File.Delete (dir </> "package-lock.json")
        Npm.install dir []        

Target.create "NpmInstall" (fun _ ->
    npmCi "."
    for dir in dirs |> Seq.map asDevel do
        if File.Exists (dir </> "package.json") then
            npmCi dir
)

Target.create "CompileCredentialManager" (fun _ ->
    Shell.cleanDir "Tasks/SetPaketCredentialProvider.dev/CredentialProvider"
    DotNet.publish (fun c ->
        { c with
            Runtime = None
            Configuration = DotNet.Release
            OutputPath = Some (Path.GetFullPath "Tasks/SetPaketCredentialProvider.dev/CredentialProvider")
        }) "CredentialProvider.PaketTeamBuild/CredentialProvider.PaketTeamBuild.fsproj"

    // Copy to all required locations
    Shell.cleanDir "Tasks/FAKE5.dev/CredentialProvider"
    Shell.cp_r "Tasks/SetPaketCredentialProvider.dev/CredentialProvider" "Tasks/FAKE5.dev/CredentialProvider"

)

Target.create "Common" (fun _ ->

    Npm.install "Common" []
    Npm.script "Common" "tsc" []
    Npm.command "Common" "pack" []

    Directory.ensure "_build"
    !! "Common/vsts-fsharp-task-common-*.tgz"
    |> Seq.iter (fun file ->
        let name = Path.GetFileName(file)
        File.Copy(file, "_build" </> name, true)
        File.Delete(file))
)


Target.create "Compile" (fun _ ->
    for dir in dirs |> Seq.map asDevel do
        if File.Exists (dir </> "package.json") then
            Npm.script dir "tsc" []
)

type ExtensionReplacement =
    { NamePostfix : string
      IdPostfix : string
      Public : bool }
    member x.AsList =
        [ "{Name-Postfix}", x.NamePostfix
          "{ID-Postfix}", x.IdPostfix
          "{Version}", version
          "\"{PublicFlag}\"", x.Public.ToString().ToLowerInvariant()
          "{Publisher}", publisher ]

let replaceInFile sourceFile targetFile (replacements: (string * string) list)=
    (File.ReadAllText(sourceFile), replacements)
    ||> Seq.fold (fun state (template, replacement) ->
        state.Replace(template, replacement))
    |> fun text -> File.WriteAllText(targetFile, text)


Target.create "SetupTaskDirectories" (fun _ ->
    // Workaround for not having an "exclude" feature...
    for dir in dirs do
        let devel = asDevel dir
        if devel <> dir then
            Shell.cleanDir dir
            Shell.cp_r devel dir
            !! (dir </> "**/*.ts")
                |> Seq.iter File.delete
    // delete stuff we don't want
    
    // cleanup node_modules to only contain --production dependencies
    for dir in dirs do
        if File.Exists (dir </> "package.json") then
            Npm.prune dir true
)


Target.create "BuildArtifacts" (fun _ ->
    Directory.ensure "publish"

    let targetName = (artifactsDir </> "tasks_temp.zip")
    dirs
    |> List.map (fun dir ->
        !! (dir + "/**/*")
            |> Zip.filesAsSpecs "Tasks")
    |> Seq.concat
    |> Zip.zipSpec targetName

    Shell.cp targetName (artifactsDir </> "tasks.zip")
    Trace.publish ImportData.BuildArtifact (artifactsDir </> "tasks.zip")
)

Target.create "RestoreArtifacts" (fun args ->
    if args.Context.TryFindPrevious "NpmInstall" |> Option.isNone then
        npmCi "."
        
    Shell.cleanDir "temp"
    Shell.cp (artifactsDir </> "tasks.zip") (artifactsDir </> "tasks_unzip.zip")
    Zip.unzip "temp" (artifactsDir </> "tasks_unzip.zip")
    File.Delete(artifactsDir </> "tasks_unzip.zip")
)

let replaceTaskJsons () =
    // fixup task-ids:
    printfn "fixing task-ids for sub-extensions."
    let replacements =
        [ "a2dadf20-1a83-4220-a4ee-b52f6c77f3cf", "dd88f622-7838-44dc-96d6-2372af78775b" // FAKE5 Runner
          "26d2a628-d5fe-4d5a-943d-33c78b2d76f3", "e5090f4d-0f56-4401-9bbc-d7af2b5c1bd1" // FAKE5 Vault
          "33416f37-5fe8-488d-a2aa-48f52e7a14f9", "1c4d173c-798c-4636-a842-2da42eb2c20e" // PaketCredentialCleanup
          "1ba72b0a-f476-4a91-90a0-b8e7a0cc4338", "90d5ae45-3fc2-4ede-b572-9a57379fbf8a" // PaketRestore
          "5bfdd7ca-9bf4-40f7-b753-fd674e7ff85c", "c2aea098-6aab-4cd3-9a0c-57b074df3df5" // SetPaketCredentialProvider
        ]

    for dir in dirs do
        let taskJson = (dir </> "task.json")
        replaceInFile taskJson taskJson replacements

Target.create "FixTaskJson" (fun _ ->
    replaceTaskJsons()
)

Target.create "BundleExtensions" (fun _ ->
    Directory.ensure (artifactsDir </> "vsix")

    // delete existing vsix files
    !! "*.vsix"
        |> Seq.iter File.Delete

    // Bundle vsix files
    let replacements = 
        [ { NamePostfix = ""; IdPostfix = ""; Public = true }
          { NamePostfix = " (Private)"; IdPostfix = "-private"; Public = false } ]
    
    !! "*-icon.png"
        |> Shell.copy "temp" 


    let createExtension ext =
        for repl in replacements do
            let sourceName = sprintf "ext-%s.json" ext
            let targetName = sprintf "temp/ext-%s%s.temp.json" ext repl.IdPostfix
            
            replaceInFile sourceName targetName repl.AsList
                
            Npm.script "." "tfx" ["extension"; "create"; "--manifest-globs"; targetName]
            File.Delete(targetName)
    
    createExtension "fsharp-helpers-extension"

    replaceTaskJsons()

    createExtension "fake-build"
    createExtension "paket"
    
    !! "*.vsix"
        |> Shell.copy (artifactsDir </> "vsix")

    !! "*.vsix"
        |> Seq.iter File.Delete
)

Target.create "Publish" (fun _ ->
    let token =
        match getVarOrDefault "vsts-token" "none" with
        | "none" ->
            match getVarOrDefault "VSTS_TOKEN" "none" with
            | "none" -> failwithf "need 'VSTS_TOKEN' to publish"
            | t -> t
        | tok -> tok
    let publishPrivate = Boolean.Parse(getVarOrDefault "publishPrivate" "false")

    let repl =
        if publishPrivate
        then { NamePostfix = " (Private)"; IdPostfix = "-private"; Public = false } 
        else { NamePostfix = ""; IdPostfix = ""; Public = true }
    
    let exts = [ "fsharp-helpers-extension"; "fake-build"; "paket"]
    for ext in exts do
        let prefix = sprintf "%s.%s%s-" publisher ext repl.IdPostfix
        let vsixFile =
           !! (artifactsDir </> "vsix" </> sprintf "%s*.vsix" prefix)
           |> Seq.filter (fun file -> 
                let name = Path.GetFileName(file)
                not <| name.Substring(prefix.Length).Contains("-"))
           |> Seq.exactlyOne
   
        Npm.script "." "tfx" ["extension"; "publish"; "--token"; token; "--vsix"; vsixFile ]
    )

Target.create "Default" (fun _ -> ())
Target.create "Publish_CD" (fun _ -> ())


"Clean"
    ==> "Common"
    ==> "NpmInstall"
    //==> "PrepareBinaries"
    ==> "Compile"
    ==> "CompileCredentialManager"
    ==> "SetupTaskDirectories"
    ==> "BuildArtifacts"

"BuildArtifacts" ?=> "RestoreArtifacts"

"RestoreArtifacts"
    ==> "BundleExtensions"
    ==> "Default"

"BuildArtifacts"
    ==> "Default"

"Default"
    ==> "Publish"

"BundleExtensions"
    ==> "Publish_CD"

Target.runOrDefault "Default"
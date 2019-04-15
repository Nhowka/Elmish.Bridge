#r "paket: groupref build //"
#load ".fake/build.fsx/intellisense.fsx"
#if !FAKE
  #r "./packages/build/NETStandard.Library.NETFramework/build/net461/lib/netstandard.dll"
#endif

#nowarn "52"

open System
open System.IO
open System.Text.RegularExpressions
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.IO.FileSystemOperators
open Fake.Tools.Git
open Fake.JavaScript

let versionFromGlobalJson : DotNet.CliInstallOptions -> DotNet.CliInstallOptions = (fun o ->
        { o with Version = DotNet.Version (DotNet.getSDKVersionFromGlobalJson()) }
    )

let dotnetSdk = lazy DotNet.install versionFromGlobalJson
let inline dtntWorkDir wd =
    DotNet.Options.lift dotnetSdk.Value
    >> DotNet.Options.withWorkingDirectory wd

let inline yarnWorkDir (ws : string) (yarnParams : Yarn.YarnParams) =
    { yarnParams with WorkingDirectory = ws }

let srcFiles =
    !! "./src/**/*.fsproj"

let root = __SOURCE_DIRECTORY__

module Util =

    let visitFile (visitor: string -> string) (fileName : string) =
        File.ReadAllLines(fileName)
        |> Array.map (visitor)
        |> fun lines -> File.WriteAllLines(fileName, lines)

    let replaceLines (replacer: string -> Match -> string option) (reg: Regex) (fileName: string) =
        fileName |> visitFile (fun line ->
            let m = reg.Match(line)
            if not m.Success
            then line
            else
                match replacer line m with
                | None -> line
                | Some newLine -> newLine)

// Module to print colored message in the console
module Logger =
    let consoleColor (fc : ConsoleColor) =
        let current = Console.ForegroundColor
        Console.ForegroundColor <- fc
        { new IDisposable with
              member x.Dispose() = Console.ForegroundColor <- current }

    let warn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.DarkYellow in printf "%s" s) str
    let warnfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.DarkYellow in printfn "%s" s) str
    let error str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printf "%s" s) str
    let errorfn str = Printf.kprintf (fun s -> use c = consoleColor ConsoleColor.Red in printfn "%s" s) str

let run (cmd:string) dir args  =
    Command.RawCommand(cmd, Arguments.OfArgs args)
    |> CreateProcess.fromCommand
    |> CreateProcess.withWorkingDirectory dir
    |> Proc.run
    |> ignore

let mono workingDir args =
    Command.RawCommand("mono", Arguments.OfArgs args)
    |> CreateProcess.fromCommand
    |> CreateProcess.withWorkingDirectory workingDir
    |> Proc.run
    |> ignore

Target.create "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "src/**/obj"
    |> Shell.cleanDirs
)

Target.create "YarnInstall"(fun _ ->
    Yarn.install id
)


Target.create "DotnetRestore" (fun _ ->
    srcFiles    
    |> Seq.iter (fun proj ->
        DotNet.restore id proj
))

let build project framework =
    DotNet.build (fun p ->
        { p with Framework = Some framework } ) project

let needsPublishing (versionRegex: Regex) (releaseNotes: ReleaseNotes.ReleaseNotes) projFile =
    printfn "Project: %s" projFile
    if releaseNotes.NugetVersion.ToUpper().EndsWith("NEXT")
    then
        Logger.warnfn "Version in Release Notes ends with NEXT, don't publish yet."
        false
    else(*
        File.ReadLines(projFile)
        |> Seq.tryPick (fun line ->
            let m = versionRegex.Match(line)
            if m.Success then Some m else None)
        |> function
            | None -> failwith "Couldn't find version in project file"
            | Some m ->
                let sameVersion = m.Groups.[1].Value = releaseNotes.NugetVersion
                if sameVersion then
                    Logger.warnfn "Already version %s, no need to publish." releaseNotes.NugetVersion
                not sameVersion
        *)
        true

let pushNuget (releaseNotes: ReleaseNotes.ReleaseNotes) (projFile: string) =
    let versionRegex = Regex("<Version>(.*?)</Version>", RegexOptions.IgnoreCase)

    if needsPublishing versionRegex releaseNotes projFile then
        let projDir = Path.GetDirectoryName(projFile)
        
        
        DotNet.pack (fun p ->
            
            { p with
                Configuration = DotNet.Release
                MSBuildParams = 
                  {p.MSBuildParams with 
                    Properties = 
                    ("PackageVersion", releaseNotes.NugetVersion)
                    ::("PackageReleaseNotes", String.concat "\n" releaseNotes.Notes)
                    ::("PackageLicenseUrl", "https://github.com/Nhowka/Elmish.Bridge/blob/master/LICENSE")
                    ::p.MSBuildParams.Properties}
                Common = { p.Common with DotNetCliPath = "dotnet" } } )
            projFile

        (versionRegex, projFile) ||> Util.replaceLines (fun line _ ->
            versionRegex.Replace(line, "<Version>" + releaseNotes.NugetVersion + "</Version>") |> Some)


        let files =
            Directory.GetFiles(projDir </> "bin" </> "Release", "*.nupkg")
            |> Array.filter (fun nupkg -> nupkg.Contains(releaseNotes.NugetVersion))
            

        match Environment.environVarOrNone "NUGET_KEY" with
        | Some nugetKey -> 
            Paket.pushFiles (fun o ->
                { o with ApiKey = nugetKey
                         PublishUrl = "https://www.nuget.org/api/v2/package" })
                files
        | None -> eprintfn "The Nuget API key must be set in a NUGET_KEY environmental variable"


        


Target.create "Publish" (fun _ ->
    srcFiles
    |> Seq.iter(fun s ->
        let projFile = s
        let release = root </> "RELEASE_NOTES.md" |> ReleaseNotes.load
        pushNuget release projFile
    )
)

"Clean"
    ==> "YarnInstall"
    ==> "DotnetRestore"
    ==> "Publish"

Target.runOrDefault "DotnetRestore"
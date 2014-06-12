#r "packages/FAKE/tools/FakeLib.dll"
open Fake

// Properties
let kspDir = lazy (ReadFileAsString "./KSPDIR")
let kspDepDir = lazy (kspDir.Force() + "/KSP_Data/Managed")
let kspDeployDir = lazy (kspDir.Force() + "/GameData/Ketchup")
let kspLocalDepDir = "./Dependencies/KSP"
let kspAssemblies = ["Assembly-CSharp.dll"; "UnityEngine.dll"]

let contribDir = "./Contrib"
let partsDir = "./Parts"

let outputDir = "./Output"
let buildDir = outputDir + "/Build"
let testDir = outputDir + "/Test"
let stageDir = outputDir + "/Stage/Ketchup"
let packageDir = outputDir + "/Package"

let buildMode = getBuildParamOrDefault "BuildMode" "Debug"

// TODO: figure out a more elegant solution
//if buildMode <> "Debug" && buildMode <> "Release"
//    then raise (System.ArgumentException("Unknown BuildMode " + buildMode))    

MSBuildDefaults <- MSBuildDefaults |> (fun p ->
    { p with
        Verbosity = Some(Minimal)
        Properties = []
    }
)

RestorePackages()

// Targets
Target "Default" (fun _ ->
    trace "Default Target"
)

Target "Init" (fun _ ->
    if not (TestDir kspLocalDepDir) then (
        CreateDir kspLocalDepDir
    )

    kspAssemblies |> List.choose (fun a ->
        match a with
        | a when not (System.IO.File.Exists (kspLocalDepDir + "/" + a)) -> Some(kspDepDir.Force() + "/" + a)
        | _ -> None
    ) |> Copy kspLocalDepDir
)

Target "BuildMod" (fun _ ->
    !! "Source/**/*.csproj"
        -- "**/*.Tests.csproj"
        |> MSBuild (buildDir + "/" + buildMode) "Build" ["Configuration", buildMode]
        |> ignore
)

Target "BuildTest" (fun _ ->
    !! "Source/**/*.Tests.csproj"
        |> MSBuildDebug testDir "Build"
        |> ignore
)

Target "Test" (fun _ ->
    !! (testDir + "/*.Tests.dll")
        |> xUnit (fun p ->
            p
        )
)

Target "Stage" (fun _ ->
    CopyDir (stageDir + "/Contrib") contribDir (fun f -> true)
    CopyDir (stageDir + "/Parts") partsDir (fun f -> true)
    CopyDir (stageDir + "/Plugins") (buildDir + "/" + buildMode) (fun f -> true)
)

Target "Deploy" (fun _ ->
    CleanDir (kspDeployDir.Force())
    CopyDir (kspDeployDir.Force()) stageDir (fun f -> true)
)

Target "Run" (fun _ ->
    // TODO: this should be asynchronous but FAKE kills the process if you use StartProcess
    ExecProcess (fun psi ->
        psi.FileName <- kspDir.Force() + "/KSP.exe"
        psi.WorkingDirectory <- kspDir.Force()
    ) (System.TimeSpan.FromMinutes 60.0) |> ignore
)

Target "Package" (fun _ ->
    // TODO: assert that this has a value
    let version = getBuildParam "Version"

    CreateDir packageDir
    !! (stageDir + "/**/*.*")
        |> Zip (stageDir + "/..") (packageDir + "/Ketchup-" + version + ".zip")
)

Target "Clean" (fun _ ->
    CleanDir outputDir
)

// Dependencies
"Init"
    ==> "Clean"
    ==> "BuildMod"
    ==> "BuildTest"
    ==> "Default"

"Default"
    ==> "Test"
    ==> "Stage"

"Stage"
    ==> "Deploy"
    ==> "Run"

"Stage"
    ==> "Package"

// Start
RunTargetOrDefault "Default"

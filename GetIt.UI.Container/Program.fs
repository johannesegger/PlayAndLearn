open GetIt
open GetIt.UI
open System
open System.Reactive.Linq
open System.Windows
open Thoth.Json.Net
open System.Reactive.Concurrency

let tryGetEnvVar = Environment.GetEnvironmentVariable >> Option.ofObj

let tryParseInt (text: string) =
    match Int32.TryParse(text) with
    | (true, value) -> Some value
    | _ -> None

let tryParseSize (text: string) =
    let parts = text.Split('x')
    let width = parts |> Array.tryItem 0 |> Option.bind tryParseInt
    let height = parts |> Array.tryItem 1 |> Option.bind tryParseInt
    match (width, height) with
    | Some width, Some height -> Some (width, height)
    | _ -> None

let tryParseUrl (text: string) =
    match Uri.TryCreate(text, UriKind.Absolute) with
    | (true, v) -> Some v
    | _ -> None

[<EntryPoint>]
[<STAThread>]
let main argv =
    let (sceneWidth, sceneHeight) = tryGetEnvVar "GET_IT_SCENE_SIZE" |> Option.bind tryParseSize |> Option.defaultValue (800, 600)
    let startMaximized = tryGetEnvVar "GET_IT_START_MAXIMIZED" |> Option.isSome
    let socketUrl = tryGetEnvVar "GET_IT_SOCKET_URL" |> Option.bind tryParseUrl
    match socketUrl with
    | Some socketUrl ->
        let mainViewModel = MainViewModel({ Width = float sceneWidth; Height = float sceneHeight }, startMaximized)
        let app = Application(MainWindow = MainWindow(DataContext = mainViewModel))
        app.MainWindow.Show()

        let (wsConnection, wsSubject) = ReactiveWebSocketClient.connect socketUrl |> Async.RunSynchronously
        use __ = wsConnection
        let uiScheduler = DispatcherScheduler(app.Dispatcher)
        use __ = MessageProcessing.run uiScheduler mainViewModel wsSubject

        app.Run() |> ignore
        printfn "UI exited."
        0
    | _ ->
        eprintfn "Missing or invalid environment variable \"GET_IT_SOCKET_URL\"."
        1

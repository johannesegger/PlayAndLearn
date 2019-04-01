namespace GetIt

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Reactive.Linq
open System.Reactive.Subjects
open System.Runtime.InteropServices
open System.Threading
open FSharp.Control.Reactive
open GetIt.Windows

exception GetItException of string

type EventHandler =
    | OnAnyKeyDown of handler: (KeyboardKey -> unit)
    | OnKeyDown of key: KeyboardKey * handler: (unit -> unit)
    | OnClickScene of handler: (Position -> MouseButton -> unit)
    | OnClickPlayer of playerId: PlayerId * handler: (MouseButton -> unit)
    | OnMouseEnterPlayer of playerId: PlayerId * handler: (unit -> unit)

type Model =
    { SceneBounds: Rectangle
      Players: Map<PlayerId, PlayerData>
      MouseState: MouseState
      KeyboardState: KeyboardState
      EventHandlers: (Guid * EventHandler) list }

module Model =
    let private gate = Object()

    let mutable private subject =
        let initial =
            { SceneBounds = { Position = Position.zero; Size = { Width = 0.; Height = 0. } }
              Players = Map.empty
              MouseState = MouseState.empty
              KeyboardState = KeyboardState.empty
              EventHandlers = [] }
        new BehaviorSubject<_>(initial)

    let observable = subject.AsObservable()

    let getCurrent () = subject.Value

    let updateCurrent fn =
        lock gate (fun () -> subject.OnNext(fn subject.Value))

    let addEventHandler eventHandler =
        let eventHandlerId = Guid.NewGuid()
        updateCurrent (fun model -> { model with EventHandlers = (eventHandlerId, eventHandler) :: model.EventHandlers })
        Disposable.create (fun () ->
            updateCurrent (fun model ->
                { model with EventHandlers = model.EventHandlers |> List.filter (fst >> (<>) eventHandlerId) }
            )
        )

module internal UICommunication =
    let private invokeEventHandlers model fn =
        model.EventHandlers
        |> List.map snd
        |> List.choose fn
        |> Async.Parallel
        |> Async.Ignore
        |> Async.Start
        model

    let private updatePlayer model playerId fn =
        let player = Map.find playerId model.Players |> fn
        { model with Players = Map.add playerId player model.Players }

    let private applyUIToControllerMessage message model =
        let invokeEventHandlers = invokeEventHandlers model
        let updatePlayer = updatePlayer model

        match message with
        | ControllerMsgProcessed -> model
        | UIEvent (SetMousePosition position) ->
            let hasBeenEntered (player: PlayerData) =
                not (Rectangle.contains model.MouseState.Position player.Bounds) &&
                Rectangle.contains position player.Bounds

            let enteredPlayerIds =
                model.Players
                |> Map.toSeq
                |> Seq.filter (snd >> hasBeenEntered)
                |> Seq.map fst
                |> Seq.toList
            invokeEventHandlers (function
                | OnMouseEnterPlayer (playerId, handler) when List.contains playerId enteredPlayerIds ->
                    Some (async { return handler () })
                | _ -> None
            )
            |> ignore

            { model with MouseState = { model.MouseState with Position = position } }
        | UIEvent (ApplyMouseClick (mouseButton, position)) ->
            let clickedPlayerIds =
                model.Players
                |> Map.toSeq
                |> Seq.filter (snd >> fun player -> Rectangle.contains position player.Bounds)
                |> Seq.map fst
                |> Seq.toList
            invokeEventHandlers (function
                | OnClickPlayer (playerId, handler) when List.contains playerId clickedPlayerIds ->
                    Some (async { return handler mouseButton })
                | _ -> None
            )
            |> ignore

            if List.isEmpty clickedPlayerIds && Rectangle.contains position model.SceneBounds
            then
                invokeEventHandlers (function
                    | OnClickScene handler ->
                        Some (async { return handler position mouseButton })
                    | _ -> None
                )
                |> ignore

            model
        | UIEvent (SetSceneBounds sceneBounds) ->
            { model with SceneBounds = sceneBounds }
        | UIEvent (AnswerQuestion (playerId, answer)) ->
            updatePlayer playerId (fun p ->
                match p.SpeechBubble with
                | Some (Ask askData) -> { p with SpeechBubble = Some (Ask { askData with Answer = Some answer }) }
                | Some (Say _)
                | None -> p
            )

    let private applyControllerToUIMessage message model =
        let updatePlayer = updatePlayer model
        let invokeEventHandlers = invokeEventHandlers model

        match message with
        | UIMsgProcessed -> model
        | ShowScene windowSize ->
            // Scene bounds will come from UI
            model
        | ClearScene ->
            model
        | AddPlayer (playerId, player) ->
            { model with Players = Map.add playerId player model.Players }
        | RemovePlayer playerId ->
            { model with Players = Map.remove playerId model.Players }
        | SetPosition (playerId, position) ->
            updatePlayer playerId (fun p -> { p with Position = position })
        | SetDirection (playerId, angle) ->
            updatePlayer playerId (fun p -> { p with Direction = angle })
        | SetSpeechBubble (playerId, speechBubble) ->
            updatePlayer playerId (fun p -> { p with SpeechBubble = speechBubble })
        | SetPen (playerId, pen) ->
            updatePlayer playerId (fun p -> { p with Pen = pen })
        | SetSizeFactor (playerId, sizeFactor) ->
            updatePlayer playerId (fun p -> { p with SizeFactor = sizeFactor })
        | SetNextCostume playerId ->
            updatePlayer playerId Player.nextCostume
        | ControllerEvent (KeyDown key) ->
            let hasActiveTextInput =
                model.Players
                |> Map.exists (fun playerId player ->
                    match player.SpeechBubble with
                    | Some (Ask askData) -> true
                    | Some (Say _)
                    | None -> false
                )
            if not hasActiveTextInput then
                invokeEventHandlers (function
                    | OnAnyKeyDown handler ->
                        Some (async { return handler key })
                    | OnKeyDown (handlerKey, handler) when handlerKey = key ->
                        Some (async { return handler () })
                    | _ -> None
                )
                |> ignore
            { model with KeyboardState = { model.KeyboardState with KeysPressed = Set.add key model.KeyboardState.KeysPressed } }
        | ControllerEvent (KeyUp key) ->
            { model with KeyboardState = { model.KeyboardState with KeysPressed = Set.remove key model.KeyboardState.KeysPressed } }
        | ControllerEvent (MouseMove position) ->
            // Position on scene will come from UI
            model
        | ControllerEvent (MouseClick (mouseButton, position)) ->
            // Position on scene will come from UI
            model

    let mutable private connection = None

    let setupLocalConnectionToUIProcess() =
        let localConnection =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                let startInfo =
#if DEBUG
                    let path =
                        let rec parentPaths path acc =
                            if isNull path then List.rev acc
                            else parentPaths (Path.GetDirectoryName path) (path :: acc)
                        parentPaths (Path.GetFullPath ".") []
                        |> Seq.choose (fun p ->
                            let projectDir = Path.Combine(p, "GetIt.WPF")
                            if Directory.Exists projectDir
                            then Some projectDir
                            else None
                        )
                        |> Seq.head
                    ProcessStartInfo("dotnet", sprintf "run --project %s" path)
#else
                    ProcessStartInfo("GetIt.WPF.exe")
#endif

                let proc = Process.Start(startInfo)

                let pipeClient = new NamedPipeClientStream(".", "GetIt", PipeDirection.InOut, PipeOptions.Asynchronous)
                pipeClient.Connect()

                MessageProcessing.forStream pipeClient ControllerToUIMsg.encode UIToControllerMsg.decode
            else
                raise (GetItException (sprintf "Operating system \"%s\" is not supported" RuntimeInformation.OSDescription))

        let subscription =
            localConnection
            |> Observable.subscribe(fun (IdentifiableMsg (msgId, msg)) ->
                Model.updateCurrent (fun model -> applyUIToControllerMessage msg model)
                localConnection.OnNext(IdentifiableMsg(msgId, UIMsgProcessed))
            )

        connection <- Some localConnection

    let sendCommand command =
        match connection with
        | Some connection ->
            match MessageProcessing.sendCommand connection command with
            | Ok msg ->
                Model.updateCurrent (applyControllerToUIMessage command >> applyUIToControllerMessage msg)
            | Error (MessageProcessing.ResponseError e) ->
                raise (GetItException (sprintf "Error while waiting for response: %O" e))
            | Error MessageProcessing.NoResponse ->
                // Close the application if the UI has been closed (throwing an exception might be confusing)
                // TODO dispose subscriptions etc. ?
                Environment.Exit 1
        | None ->
            raise (GetItException "Connection to UI not set up.")

type Player(playerId) =
    let mutable isDisposed = 0

    member internal x.PlayerId with get () = playerId
    member private x.Player with get () = Map.find playerId (Model.getCurrent().Players)
    /// <summary>
    /// The actual size of the player.
    /// </summary>
    member x.Size with get () = x.Player.Size

    /// <summary>
    /// The factor that is used to resize the player.
    /// </summary>
    member x.SizeFactor with get () = x.Player.SizeFactor

    /// <summary>
    /// The position of the player.
    /// </summary>
    member x.Position with get () = x.Player.Position

    /// <summary>
    /// The actual bounds of the player.
    /// </summary>
    member x.Bounds with get () = x.Player.Bounds

    /// <summary>
    /// The direction of the player.
    /// </summary>
    member x.Direction with get () = x.Player.Direction

    /// <summary>
    /// The pen of the player.
    /// </summary>
    member x.Pen with get () = x.Player.Pen

    abstract member Dispose: unit -> unit
    default x.Dispose () =
        if Interlocked.Exchange (&isDisposed, 1) = 0 then
            UICommunication.sendCommand (RemovePlayer playerId)

    interface IDisposable with
        member x.Dispose () = x.Dispose ()

module internal Game =
    let mutable defaultTurtle = None

[<AbstractClass; Sealed>]
type Game() =
    static member ShowScene () =
        UICommunication.setupLocalConnectionToUIProcess()

        let windowSize = { Width = 800.; Height = 600. }
        UICommunication.sendCommand (ShowScene windowSize)

        let subject = new System.Reactive.Subjects.Subject<_>()
        let (mouseMoveObservable, otherEventsObservable) =
            subject
            |> Observable.split (function
                | MouseMove _ as x -> Choice1Of2 x
                | x -> Choice2Of2 x
            )
        let d1 =
            mouseMoveObservable
            |> Observable.sample (TimeSpan.FromMilliseconds 50.)
            |> Observable.subscribe (ControllerEvent >> UICommunication.sendCommand)

        let d2 =
            otherEventsObservable
            |> Observable.subscribe (ControllerEvent >> UICommunication.sendCommand)

        let d3 =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                GetIt.Windows.DeviceEvents.register subject
            else
                raise (GetItException (sprintf "Operating system \"%s\" is not supported" RuntimeInformation.OSDescription))

        ()

    static member AddPlayer (playerData: PlayerData, run: Action<_>) =
        let playerId = PlayerId.create ()
        UICommunication.sendCommand (AddPlayer (playerId, playerData))
        let player = new Player(playerId)
        async { run.Invoke player }
        |> Async.Start
        player

    static member ShowSceneAndAddTurtle () =
        Game.ShowScene ()
        let turtleId = PlayerId.create ()
        UICommunication.sendCommand (AddPlayer (turtleId, Player.turtle))
        Game.defaultTurtle <- Some (new Player (turtleId))

    static member ClearScene () =
        UICommunication.sendCommand ClearScene

    static member Sleep (durationInMilliseconds) =
        Thread.Sleep (TimeSpan.FromMilliseconds (durationInMilliseconds))

    static member OnAnyKeyDown (action: Action<_>) =
        Model.addEventHandler (OnAnyKeyDown action.Invoke)

    static member OnKeyDown (key, action: Action) =
        Model.addEventHandler (OnKeyDown (key, action.Invoke))

    static member OnClickScene (action: Action<_, _>) =
        Model.addEventHandler (OnClickScene (curry action.Invoke))

    static member SceneBounds
        with get() = Model.getCurrent().SceneBounds

namespace GetIt

open ColorCode
open FSharp.Control.Reactive
open Fue.Compiler
open Fue.Data
open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Text
open System.Threading

module internal Game =
    let mutable defaultTurtle = None

/// Defines methods to setup a game, add players, register global events and more.
[<AbstractClass; Sealed>]
type Game() =
    /// Initializes and shows an empty scene with the default size and no players on it.
    static member ShowScene () =
        UICommunication.showScene (SpecificSize { Width = 800.; Height = 600. })

    /// Initializes and shows an empty scene with a specific size and no players on it.
    static member ShowScene (windowWidth, windowHeight) =
        UICommunication.showScene (SpecificSize { Width = windowWidth; Height = windowHeight })

    /// Initializes and shows an empty scene with maximized size and no players on it.
    static member ShowMaximizedScene () =
        UICommunication.showScene Maximized

    /// <summary>
    /// Adds a player to the scene.
    /// </summary>
    /// <param name="player">The definition of the player that should be added.</param>
    /// <returns>The added player.</returns>
    static member AddPlayer (playerData: PlayerData) =
        if obj.ReferenceEquals(playerData, null) then raise (ArgumentNullException "playerData")

        let playerId = UICommunication.addPlayer playerData
        new Player(playerId)

    /// <summary>
    /// Adds a player to the scene and calls a method to control the player.
    /// The method runs on a thread pool thread so that multiple players can be controlled in parallel.
    /// </summary>
    /// <param name="player">The definition of the player that should be added.</param>
    /// <param name="run">The method that is used to control the player.</param>
    /// <returns>The added player.</returns>
    static member AddPlayer (playerData, run: Action<_>) =
        if obj.ReferenceEquals(playerData, null) then raise (ArgumentNullException "playerData")
        if obj.ReferenceEquals(run, null) then raise (ArgumentNullException "run")

        let player = Game.AddPlayer playerData
        async { run.Invoke player } |> Async.Start
        player

    static member private AddTurtle() =
        Game.defaultTurtle <- Some <| Game.AddPlayer PlayerData.Turtle

    /// Initializes and shows an empty scene and adds the default player to it.
    static member ShowSceneAndAddTurtle () =
        Game.ShowScene ()
        Game.AddTurtle ()

    /// Initializes and shows an empty scene with a specific size and adds the default player to it.
    static member ShowSceneAndAddTurtle (windowWidth, windowHeight) =
        Game.ShowScene (windowWidth, windowHeight)
        Game.AddTurtle ()

    /// Initializes and shows an empty scene with maximized size and adds the default player to it.
    static member ShowMaximizedSceneAndAddTurtle () =
        Game.ShowMaximizedScene ()
        Game.AddTurtle ()

    /// Sets the title of the window.
    static member SetWindowTitle text =
        let textOpt = if String.IsNullOrWhiteSpace text then None else Some text
        UICommunication.setWindowTitle textOpt

    /// Sets the scene background.
    static member SetBackground background =
        if obj.ReferenceEquals(background, null) then raise (ArgumentNullException "background")

        UICommunication.setBackground background

    /// Clears all drawings from the scene.
    static member ClearScene () =
        UICommunication.clearScene ()

    /// <summary>
    /// Prints the scene. Note that `wkhtmltopdf` and `SumatraPDF` must be installed.
    /// </summary>
    /// <param name="printConfig">The configuration used for printing.</param>
    static member Print printConfig =
        if obj.ReferenceEquals(printConfig, null) then raise (ArgumentNullException "printConfig")

        if not <| RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            raise (GetItException (sprintf "Printing is not supported on operating system \"%s\"." RuntimeInformation.OSDescription))

        let assemblyDir =
            Assembly.GetCallingAssembly().Location
            |> Path.GetDirectoryName

        let base64ImageData =
            UICommunication.makeScreenshot ()
            |> PngImage.toBase64String

        let documentTemplate =
            try
                File.ReadAllText printConfig.TemplatePath
            with e -> raise (GetItException ("Error while printing scene: Can't read print template.", e))

        let documentContent =
            let configParams =
                printConfig.TemplateParams
                |> Map.map (fun key value -> value :> obj)
                |> Map.toList
            let sourceFiles =
                let sourceFilesDir = Path.Combine(assemblyDir, "src")
                Directory.GetFiles(sourceFilesDir, "*.source", SearchOption.AllDirectories)
                |> Seq.map (fun f ->
                    let relativePath =
                        f.Substring(sourceFilesDir.Length).TrimStart('/', '\\')
                        |> fun p -> Path.ChangeExtension(p, null)
                    let content = File.ReadAllText f
                    let formattedContent = HtmlFormatter().GetHtmlString(content, Languages.CSharp)
                    relativePath, formattedContent
                )
                |> Seq.toList
            init
            |> addMany configParams
            |> add "sourceFiles" sourceFiles
            |> add "screenshot" base64ImageData
            |> fromText documentTemplate

        let pdfPath = Path.Combine(Path.GetTempPath(), sprintf "%O.pdf" (Guid.NewGuid()))
        do
            let htmlPath = Path.Combine(Path.GetTempPath(), sprintf "%O.html" (Guid.NewGuid()))
            File.WriteAllText(htmlPath, documentContent, Encoding.UTF8)
            use d = Disposable.create (fun () -> try File.Delete(htmlPath) with _ -> ())

            let wkHtmlToPdfStartInfo = ProcessStartInfo("wkhtmltopdf", sprintf "\"%s\" \"%s\"" htmlPath pdfPath)
            let exitCode =
                try
                    use wkHtmlToPdfProcess = Process.Start(wkHtmlToPdfStartInfo)
                    wkHtmlToPdfProcess.WaitForExit()
                    wkHtmlToPdfProcess.ExitCode
                with e -> raise (GetItException ("Error while printing scene: Ensure `wkhtmltopdf` is installed", e))
            if exitCode <> 0 then
                raise (GetItException (sprintf "wkhtmltopdf exited with non-zero exit code (%d)." exitCode))
        do
            use d = Disposable.create (fun () -> try File.Delete(pdfPath) with _ -> ())

            let sumatraStartInfo = ProcessStartInfo("sumatrapdf", sprintf "-print-to \"%s\" -print-settings \"duplex\" -silent -exit-when-done \"%s\"" printConfig.PrinterName pdfPath)
            let exitCode =
                try
                    use sumatraProcess = Process.Start(sumatraStartInfo)
                    sumatraProcess.WaitForExit()
                    sumatraProcess.ExitCode
                with e -> raise (GetItException ("Error while printing scene: Ensure `sumatrapdf` is installed.", e))
            if exitCode <> 0 then
                raise (GetItException (sprintf "SumatraPDF exited with non-zero exit code (%d). Ensure that printer \"%s\" is connected." exitCode printConfig.PrinterName))

    /// Start batching multiple commands to skip drawing intermediate state.
    /// Note that commands from all threads are batched.
    static member BatchCommands () =
        UICommunication.startBatch ()
        Disposable.create (fun () -> UICommunication.applyBatch ())

    /// <summary>
    /// Pauses execution of the current thread for a given time.
    /// </summary>
    /// <param name="duration">The length of the pause.</param>
    static member Sleep (duration: TimeSpan) =
        Thread.Sleep duration

    /// <summary>
    /// Pauses execution of the current thread for a given time.
    /// </summary>
    /// <param name="durationInMilliseconds">The length of the pause in milliseconds.</param>
    static member Sleep durationInMilliseconds =
        Game.Sleep (TimeSpan.FromMilliseconds durationInMilliseconds)

    /// <summary>
    /// Pauses execution until the mouse clicks at the scene.
    /// </summary>
    /// <returns>The position of the mouse click.</returns>
    static member WaitForMouseClick () =
        use signal = new ManualResetEventSlim()
        let mutable mouseClickEvent = None
        let fn ev =
            mouseClickEvent <- Some ev
            signal.Set()
        use d = Model.onClickScene fn
        signal.Wait()
        Option.get mouseClickEvent

    /// <summary>
    /// Pauses execution until a specific keyboard key is pressed.
    /// </summary>
    /// <param name="key">The keyboard key to wait for.</param>
    static member WaitForKeyDown key =
        if obj.ReferenceEquals(key, null) then raise (ArgumentNullException "key")

        use signal = new ManualResetEventSlim()
        use d = Model.onKeyDown key signal.Set
        signal.Wait()

    /// <summary>
    /// Pauses execution until any keyboard key is pressed.
    /// </summary>
    /// <returns>The keyboard key that is pressed.</returns>
    static member WaitForAnyKeyDown () =
        use signal = new ManualResetEventSlim()
        let mutable keyboardKey = None
        let fn key =
            keyboardKey <- Some key
            signal.Set()
        use d = Model.onAnyKeyDown fn
        signal.Wait()
        Option.get keyboardKey

    /// <summary>
    /// Checks whether a given keyboard key is pressed.
    /// </summary>
    /// <param name="key">The keyboard key.</param>
    /// <returns>True, if the keyboard key is pressed, otherwise false.</returns>
    static member IsKeyDown key =
        if obj.ReferenceEquals(key, null) then raise (ArgumentNullException "key")

        Model.getCurrent().KeyboardState.KeysPressed
        |> Set.contains key

    /// <summary>
    /// Checks whether any keyboard key is pressed.
    /// </summary>
    /// <returns>True, if any keyboard key is pressed, otherwise false.</returns>
    static member IsAnyKeyDown
        with get () =
            Model.getCurrent().KeyboardState.KeysPressed
            |> Set.isEmpty
            |> not

    /// <summary>
    /// Registers an event handler that is called once when any keyboard key is pressed.
    /// </summary>
    /// <param name="action">The event handler that should be called.</param>
    /// <returns>The disposable subscription.</returns>
    static member OnAnyKeyDown (action: Action<_>) =
        if obj.ReferenceEquals(action, null) then raise (ArgumentNullException "action")

        Model.onAnyKeyDown action.Invoke

    /// <summary>
    /// Registers an event handler that is called once when a specific keyboard key is pressed.
    /// </summary>
    /// <param name="key">The keyboard key that should be listened to.</param>
    /// <param name="action">The event handler that should be called.</param>
    /// <returns>The disposable subscription.</returns>
    static member OnKeyDown (key, action: Action) =
        if obj.ReferenceEquals(key, null) then raise (ArgumentNullException "key")
        if obj.ReferenceEquals(action, null) then raise (ArgumentNullException "action")

        Model.onKeyDown key action.Invoke

    /// <summary>
    /// Registers an event handler that is called continuously when any keyboard key is pressed.
    /// </summary>
    /// <param name="interval">How often the event handler should be called.</param>
    /// <param name="action">The event handler that should be called.</param>
    /// <returns>The disposable subscription.</returns>
    static member OnAnyKeyDown (interval, action: Action<_, _>) =
        if obj.ReferenceEquals(action, null) then raise (ArgumentNullException "action")

        Model.whileAnyKeyDown interval (curry action.Invoke)

    /// <summary>
    /// Registers an event handler that is called continuously when a specific keyboard key is pressed.
    /// </summary>
    /// <param name="key">The keyboard key that should be listened to.</param>
    /// <param name="interval">How often the event handler should be called.</param>
    /// <param name="action">The event handler that should be called.</param>
    /// <returns>The disposable subscription.</returns>
    static member OnKeyDown (key, interval, action: Action<_>) =
        if obj.ReferenceEquals(action, null) then raise (ArgumentNullException "action")

        Model.whileKeyDown key interval action.Invoke

    /// <summary>
    /// Registers an event handler that is called when the mouse is clicked anywhere on the scene.
    /// </summary>
    /// <param name="action">The event handler that should be called.</param>
    /// <returns>The disposable subscription.</returns>
    static member OnClickScene (action: Action<_>) =
        if obj.ReferenceEquals(action, null) then raise (ArgumentNullException "action")

        Model.onClickScene action.Invoke

    /// The bounds of the scene.
    static member SceneBounds
        with get () = Model.getCurrent().SceneBounds

    /// The current position of the mouse.
    static member MousePosition
        with get () = Model.getCurrent().MouseState.Position


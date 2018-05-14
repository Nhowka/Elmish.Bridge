namespace Elmish.Remoting
[<RequireQualifiedAccess>]
module ClientProgram =
  open Elmish
  open Fable
  open Fable.Core
  /// Transforms a ClientProgram's `update` function into an Elmish-compatible `update` function.
  /// Used with Elmish's `mkProgram`
  let updateBridge update = fun ms md -> match ms with C msg -> update msg md | _ -> md, Cmd.none
  /// Defines a message to be sent when the client gets connected to the server
  let onConnectionOpen msg program = {program with onConnectionOpen = Some msg }
  /// Defines a message to be sent when the client gets disconnected to the server
  let onConnectionLost msg program = {program with onConnectionLost = Some msg }
  /// Creates a `ClientProgram` from an Elmish's `Program`
  let fromProgram (program:Program<_,_,Msg<'server,'client>,_>) = {
    program = program
    onConnectionOpen = None
    onConnectionLost = None}

  [<PassGenerics>]
  /// Creates the program loop with a websocket connection
  /// `server`: websocket endpoint
  /// `arg`: argument to the `init` function
  /// `program`: A `ClientProgram` created with Elmish's `mkProgram` and passed to `ClientProgram.fromProgram`
  let runAtWith server (arg: 'arg) (program: ClientProgram<'arg, 'model, 'server, 'client, 'view>) =
        let (model,cmd) = program.program.init arg
        let url = Fable.Import.Browser.URL.Create(Fable.Import.Browser.window.location.href)
        url.protocol <- url.protocol.Replace ("http","ws")
        url.pathname <- server

        let ws = ref None
        let inbox = MailboxProcessor.Start(fun (mb:MailboxProcessor<_>) ->
            let rec loop (state:'model) =
                async {
                    let! msg = mb.Receive()
                    let newState =
                        try
                            match msg with
                            |C msg ->
                                let (model',cmd') = program.program.update (C msg) state
                                program.program.setState model' mb.Post
                                cmd' |> List.iter (fun sub -> sub mb.Post)
                                model'
                            | S msg ->
                                !ws |> Option.iter (
                                    fun (s:Fable.Import.Browser.WebSocket) ->
                                        s.send(JsInterop.toJson msg))
                                state
                        with ex ->
                            program.program.onError ("Unable to process a message:", ex)
                            state
                    return! loop newState
                }
            loop model
        )
        program.program.setState model inbox.Post
        let rec websocket server r =
            let ws = Fable.Import.Browser.WebSocket.Create server //url.href
            r := Some ws
            ws.onopen <- fun _ ->
                program.onConnectionOpen |> Option.iter (C >> inbox.Post)
            ws.onclose <- fun _ ->
                program.onConnectionLost |> Option.iter (C >> inbox.Post)
                Fable.Import.Browser.window.setTimeout(websocket server r, 1000) |> ignore
            ws.onmessage <- fun e ->
                e.data |> string |> JsInterop.ofJson |> C |> inbox.Post
        websocket url.href ws
        let sub =
            try
                program.program.subscribe model
            with ex ->
                program.program.onError ("Unable to subscribe:", ex)
                Cmd.none
        sub @ cmd |> List.iter (fun sub -> sub inbox.Post)
  /// Creates the program loop with a websocket connection passing `unit` to the `init` function
  /// `server`: websocket endpoint
  /// `arg`: argument to the `init` function
  /// `program`: A `ClientProgram` created with Elmish's `mkProgram` and passed to `ClientProgram.fromProgram`

  let runAt server (program: ClientProgram<unit, 'model, 'server, 'client, 'view>) =
    runAtWith server () program


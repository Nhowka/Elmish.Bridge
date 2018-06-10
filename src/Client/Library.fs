namespace Elmish.Remoting
open Elmish
open System
/// Defines client configuration
type ClientProgram<'arg,'model,'server,'originalclient,'client,'view> = {
    program : Program<'arg,'model,'client,'view>
    mapClientMsg : 'originalclient -> 'client
    serverDispatch : Dispatch<'server> option ref
    onConnectionOpen : 'originalclient option
    onConnectionLost : 'originalclient option
  }

[<RequireQualifiedAccess>]
module RemoteProgram =
  type ServerMsgException<'msg>(msg) =
    inherit Exception()

    member __.Msg : 'msg = msg

  open Fable
  open Fable.Core
  /// Creates a remote program
  let mkProgram (init:'arg -> 'model * Cmd<Msg<'server,'originalclient>>) (update:'originalclient -> 'model -> 'model * Cmd<Msg<'server,'originalclient>>) (view:'model -> Dispatch<Msg<'server,'originalclient>> -> 'view)
                : ClientProgram<'arg,'model,'server,'originalclient,'originalclient,'view> =

    let serverDispatch = ref None

    let msgTransformer  = function C msg -> msg | S msg -> raise (ServerMsgException(msg))

    let programTransformer (model, cmds) =
        model, cmds |> Cmd.map msgTransformer

    let init = (init>>programTransformer)

    let update msg model =
        let (model, cmds) = update msg model
        model, cmds |> Cmd.map msgTransformer

    let program = Program.mkProgram init update (fun model v -> view model (function C msg -> v msg | S msg -> !serverDispatch |> Option.iter(fun v -> v msg)))
    {
        program = program
        serverDispatch = serverDispatch
        mapClientMsg = id
        onConnectionOpen = None
        onConnectionLost = None
    }

  /// Defines a bridge to transformation of Elmish programs that change the type of the client message
  let programBridgeWithMsgMapping (mapping:'client -> 'newclient) (mapper:Program<'arg,'model,'client,'view> -> Program<'newarg,'newmodel,'newclient,'newview> ) (clientProgram: ClientProgram<'arg,'model,'server,'originalclient,'client,'view>)
    : ClientProgram<'newarg,'newmodel,'server,'originalclient,'newclient,'newview> =
    {
        program = mapper clientProgram.program
        mapClientMsg = clientProgram.mapClientMsg >> mapping
        serverDispatch = clientProgram.serverDispatch
        onConnectionLost = clientProgram.onConnectionLost
        onConnectionOpen = clientProgram.onConnectionOpen
    }
  /// Defines a bridge to transformation of Elmish programs that don't change the type of the client message
  let programBridge mapper clientProgram =
    {
        program = mapper clientProgram.program
        mapClientMsg = clientProgram.mapClientMsg
        serverDispatch = clientProgram.serverDispatch
        onConnectionLost = clientProgram.onConnectionLost
        onConnectionOpen = clientProgram.onConnectionOpen
    }

  /// Defines a message to be sent when the client gets connected to the server
  let onConnectionOpen msg program = {program with onConnectionOpen = Some msg }
  /// Defines a message to be sent when the client gets disconnected to the server
  let onConnectionLost msg program = {program with onConnectionLost = Some msg }

  let private normalize (program: ClientProgram<'arg, 'model, 'server,'originalclient, 'client, 'view>)
        : ClientProgram<'arg, 'model, 'server,'originalclient, Msg<'server,'client>, 'view> =

    let clientInit arg =
        let model, cmds = program.program.init arg
        model, cmds |> Cmd.map C

    let clientUpdate msg model =
        let model, cmds = program.program.update msg model
        model, cmds |> Cmd.map C

    let clientSubscribe model =
        program.program.subscribe model |> Cmd.map C

    let clientView model dispatch =
        program.program.view model (C >> dispatch)


    let clientSetState model dispatch =
        program.program.setState model (C >> dispatch)

    {
        program =
            {
                update = fun ms md -> match ms with C msg -> clientUpdate msg md | _ -> md, Cmd.none
                subscribe = clientSubscribe
                init = clientInit
                view = clientView
                setState = clientSetState
                onError = program.program.onError
            }
        mapClientMsg = program.mapClientMsg >> C
        serverDispatch = program.serverDispatch
        onConnectionLost = program.onConnectionLost
        onConnectionOpen = program.onConnectionOpen
    }


  [<PassGenerics>]
  /// Creates the program loop with a websocket connection
  /// `server`: websocket endpoint
  /// `arg`: argument to the `init` function
  /// `program`: A `ClientProgram` created with `RemoteProgram.mkProgram`
  let runAtWith server (arg: 'arg) (program: ClientProgram<'arg, 'model, 'server,'originalclient, 'client, 'view>) =
        let program = normalize program

        let (model,cmd) = program.program.init arg
        let url = Fable.Import.Browser.URL.Create(Fable.Import.Browser.window.location.href)
        url.protocol <- url.protocol.Replace ("http","ws")
        url.pathname <- server

        let ws = ref None
        let safe dispatch =
            fun sub ->
                try
                    sub dispatch
                with
                | :? ServerMsgException<'server> as ex ->
                    dispatch (S ex.Msg)

        let inbox = MailboxProcessor.Start(fun (mb:MailboxProcessor<Msg<'server,'client>>) ->
            let rec loop (state:'model) =
                async {
                    let! msg = mb.Receive()
                    let newState =
                        try
                            match msg with
                            |C msg ->
                                let (model',cmd') = program.program.update (C msg) state
                                program.program.setState model' mb.Post
                                cmd' |> List.iter (safe mb.Post)
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
        program.serverDispatch := Some (fun msg -> (inbox.Post (S msg)))
        program.program.setState model inbox.Post
        let rec websocket server r =
            let ws = Fable.Import.Browser.WebSocket.Create server
            r := Some ws
            ws.onopen <- fun _ ->
                program.onConnectionOpen |> Option.iter (program.mapClientMsg >> inbox.Post)
            ws.onclose <- fun _ ->
                program.onConnectionLost |> Option.iter (program.mapClientMsg >> inbox.Post)
                Fable.Import.Browser.window.setTimeout(websocket server r, 1000) |> ignore
            ws.onmessage <- fun e ->
                e.data |> string |> JsInterop.ofJson |> program.mapClientMsg |> inbox.Post
        websocket url.href ws
        let sub =
            try
                program.program.subscribe model
            with ex ->
                program.program.onError ("Unable to subscribe:", ex)
                Cmd.none
        sub @ cmd |> List.iter (safe inbox.Post)
  /// Creates the program loop with a websocket connection passing `unit` to the `init` function
  /// `server`: websocket endpoint
  /// `program`: A `RemoteProgram` created with Elmish.Remotigs's `RemoteProgram.mkProgram`
  [<PassGenerics>]
  let runAt server (program: ClientProgram<unit, 'model, 'server,'originalclient,'client, 'view>) =
    runAtWith server () program


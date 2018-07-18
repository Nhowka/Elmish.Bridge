namespace Elmish.Bridge
open Elmish
open System
/// Defines client configuration
type ClientProgram<'arg,'model,'server,'originalclient,'client,'view> = {
    program : Program<'arg,'model,'client,'view>
    mapClientMsg : 'originalclient -> 'client
    serverDispatch : Dispatch<'server> option ref
    whenDown : 'originalclient option
    subscribe : unit -> Cmd<Msg<'server,'originalclient>>
    endpoint : string
  }

[<RequireQualifiedAccess>]
module Bridge =
  type internal ServerMsgException<'msg>(msg) =
    inherit Exception()

    member __.Msg : 'msg = msg

  open Fable
  open Fable.Core
  /// Creates a remote program
  let mkClient (init:'arg -> 'model * Cmd<Msg<'server,'originalclient>>) (update:'originalclient -> 'model -> 'model * Cmd<Msg<'server,'originalclient>>) (view:'model -> Dispatch<Msg<'server,'originalclient>> -> 'view)
                : ClientProgram<'arg,'model,'server,'originalclient,'originalclient,'view> =

    let serverDispatch = ref None

    let msgTransformer  = function C msg -> msg | S msg -> raise (ServerMsgException(msg))

    let programTransformer (model, cmds) =
        model, cmds |> Cmd.map msgTransformer

    let init = (init >> programTransformer)

    let update msg model =
        let (model, cmds) = update msg model
        model, cmds |> Cmd.map msgTransformer

    let program = Program.mkProgram init update (fun model v -> view model (function C msg -> v msg | S msg -> !serverDispatch |> Option.iter(fun v -> v msg)))
    {
        program = program
        serverDispatch = serverDispatch
        subscribe = fun () -> Cmd.none
        mapClientMsg = id
        whenDown = None
        endpoint = ""
    }
  /// Defines the endpoint where the program will run
  let at endpoint clientProgram =
    {clientProgram with endpoint = endpoint}

  /// Defines a transformation of Elmish programs that changes the type of the client message
  let mapped (mapping:'client -> 'newclient) (mapper:Program<'arg,'model,'client,'view> -> Program<'newarg,'newmodel,'newclient,'newview> ) (clientProgram: ClientProgram<'arg,'model,'server,'originalclient,'client,'view>)
    : ClientProgram<'newarg,'newmodel,'server,'originalclient,'newclient,'newview> =
    {
        program = mapper clientProgram.program
        mapClientMsg = clientProgram.mapClientMsg >> mapping
        serverDispatch = clientProgram.serverDispatch
        subscribe = clientProgram.subscribe
        whenDown = clientProgram.whenDown
        endpoint = clientProgram.endpoint
    }

  /// Defines a transformation of Elmish programs that don't change the type of the client message
  let simple mapper clientProgram =
    {
        program = mapper clientProgram.program
        mapClientMsg = clientProgram.mapClientMsg
        serverDispatch = clientProgram.serverDispatch
        subscribe = clientProgram.subscribe
        whenDown = clientProgram.whenDown
        endpoint = clientProgram.endpoint
    }

  /// Defines a message to be sent when the client gets disconnected to the server
  let whenDown msg program = { program with whenDown = Some msg }

  /// Defines a subcriber that can send server messages
  /// That's different from the Elmish subscriber as it takes unit instead of the model as it can be
  /// changed unpredictably when using the program mappers

  let withSubscription sub program=
    let sub () =
        Cmd.batch [
            program.subscribe ()
            sub ()
        ]
    { program with subscribe = sub }

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
        subscribe = program.subscribe
        mapClientMsg = program.mapClientMsg >> C
        serverDispatch = program.serverDispatch
        whenDown = program.whenDown
        endpoint = program.endpoint
    }

  [<PassGenerics>]
  /// Creates the program loop with a websocket connection
  /// `arg`: argument to the `init` function
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkClient`
  let runWith (arg: 'arg) (program: ClientProgram<'arg, 'model, 'server,'originalclient, 'client, 'view>) =
        let program = normalize program

        let (model,cmd) = program.program.init arg
        let url = Fable.Import.Browser.URL.Create(Fable.Import.Browser.window.location.href)
        url.protocol <- url.protocol.Replace ("http","ws")
        url.pathname <- program.endpoint

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
                            | C msg ->
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
            ws.onclose <- fun _ ->
                program.whenDown |> Option.iter (program.mapClientMsg >> inbox.Post)
                Fable.Import.Browser.window.setTimeout(websocket server r, 1000) |> ignore
            ws.onmessage <- fun e ->
                e.data |> string |> JsInterop.ofJson |> program.mapClientMsg |> inbox.Post

        let urlNoHash = (url.href.Split '#').[0] 
        websocket urlNoHash ws

        let serverSub =
            try
                program.subscribe () |> Cmd.map (function C msg -> program.mapClientMsg msg | S msg -> S msg)
            with ex ->
                program.program.onError ("Unable to subscribe:", ex)
                Cmd.none
        let sub =
            try
                program.program.subscribe model
            with ex ->
                program.program.onError ("Unable to subscribe:", ex)
                Cmd.none
        serverSub @ sub @ cmd |> List.iter (safe inbox.Post)
  /// Creates the program loop with a websocket connection passing `unit` to the `init` function at the defined endpoint
  /// `server`: websocket endpoint
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkClient`
  [<PassGenerics>]
  let runAt server (program: ClientProgram<unit, 'model, 'server,'originalclient,'client, 'view>) =
    runWith () { program with endpoint = server }

  /// Creates the program loop with a websocket connection at the defined endpoint
  /// `server`: websocket endpoint
  /// `arg`: argument to the `init` function
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkClient`
  [<PassGenerics>]
  let runAtWith server arg (program: ClientProgram<unit, 'model, 'server,'originalclient,'client, 'view>) =
    runWith arg { program with endpoint = server }

  /// Creates the program loop with a websocket connection passing `unit` to the `init` function
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkClient`
  [<PassGenerics>]
  let run (program: ClientProgram<unit, 'model, 'server,'originalclient,'client, 'view>) =
    runWith () program

[<AutoOpen>]
module CE =
    open Fable.Core
    type ClientBuilder<'arg,'model,'server,'originalclient,'client,'view>(init,update,view) =
        let zero : ClientProgram<'arg,'model,'server,'originalclient,'originalclient,'view> =
            Bridge.mkClient init update view

        member __.Yield(_) = zero
        member __.Zero() = zero
        [<CustomOperation("whenDown")>]
        /// Takes a client message to be send when the connection with the server is lost
        member __.WhenDown(clientProgram,whenDown) = Bridge.whenDown whenDown clientProgram
        [<CustomOperation("simple")>]
        /// Takes an Elmish `Program<_,_,msg,_> -> Program<_,_,msg,_>` for
        /// compatibility with Elmish libraries
        member __.Simple(clientProgram,mapper) = Bridge.simple mapper clientProgram
        [<CustomOperation("mapped")>]
        /// Takes an `msg -> newMsg` Elmish `Program<_,_,msg,_> -> Program<_,_,newMsg,_>` for
        /// compatibility with Elmish libraries that modify the message type
        member __.Mapped(clientProgram,map,mapper) = Bridge.mapped map mapper clientProgram
        [<CustomOperation("at")>]
        /// Takes a `string` defining where the client will connect to the server
        member __.At(clientProgram,endpoint) = Bridge.at endpoint clientProgram
        [<CustomOperation("sub")>]
        /// Takes an `unit -> Cmd<Msg<'server,'client>>` to be possible to send server messages
        /// reacting to external events
        member __.WithSubscription(clientProgram,sub) = Bridge.withSubscription sub clientProgram
        [<CustomOperation("runWith")>]
        [<PassGenerics>]
        member __.RunWith(clientProgram,arg) = Bridge.runWith arg clientProgram
        member __.Run(_:unit) = ()
        [<PassGenerics>]
        member __.Run(clientProgram) = Bridge.run clientProgram

    /// Creates the client
    let bridge init update view = ClientBuilder(init, update, view)
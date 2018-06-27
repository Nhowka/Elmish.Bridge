namespace Elmish.Bridge
open Elmish.XamarinForms
open System
open System.Net.WebSockets
open System.Threading
open Newtonsoft.Json
open Elmish.XamarinForms.StaticViews

type internal ViewMode<'model, 'server, 'client> =
    | DynamicView of ('model -> (Msg<'server,'client> -> unit) -> DynamicViews.ViewElement)
    | StaticView of (unit -> (Xamarin.Forms.Page*StaticViews.ViewBindings<'model,'client>))

type InternalProgram<'model, 'client> =
    | DynamicProgram of Program<'model,'client, ('model -> ('client -> unit) -> DynamicViews.ViewElement)>
    | StaticProgram of Program<'model, 'client, (unit -> (Xamarin.Forms.Page*StaticViews.ViewBindings<'model,'client>))>

type internal Runner<'model, 'client> =
    | DynamicRunner of ProgramRunner<'model,'client>
    | StaticRunner of StaticView.StaticViewProgramRunner<'model,'client>

/// Defines client configuration
type ClientProgram<'model,'server,'originalclient,'client> = {
    program : InternalProgram<'model,'client>
    application : Xamarin.Forms.Application option
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
  let internal mkClient (init:unit -> 'model * Cmd<Msg<'server,'originalclient>>) (update:'originalclient -> 'model -> 'model * Cmd<Msg<'server,'originalclient>>) view
                : ClientProgram<'model,'server,'originalclient,'originalclient> =

    let serverDispatch = ref None

    let msgTransformer  = function C msg -> msg | S msg -> raise (ServerMsgException(msg))

    let programTransformer (model, cmds) =
        model, cmds |> Cmd.map msgTransformer

    let init = (init >> programTransformer)

    let update msg model =
        let (model, cmds) = update msg model
        model, cmds |> Cmd.map msgTransformer

    let program =
        match view with
        | DynamicView view ->
            DynamicProgram <| Program.mkProgram init update (fun model v -> view model (function C msg -> v msg | S msg -> !serverDispatch |> Option.iter(fun v -> v msg)))
        | StaticView view ->
            StaticProgram <| Program.mkProgram init update view
    {
        program = program
        serverDispatch = serverDispatch
        subscribe = fun () -> Cmd.none
        mapClientMsg = id
        whenDown = None
        endpoint = ""
        application = None
    }
  /// Create a remote program using dynamic views
  let mkDynamicClient init update view =
    mkClient init update (DynamicView view)

    /// Create a remote program using dynamic views
  let mkStaticClient init update view =
    mkClient init update (StaticView (fun () -> let (p,b) = view () in (p :> Xamarin.Forms.Page),b))


  /// Defines the endpoint where the program will run
  let at endpoint clientProgram =
    {clientProgram with endpoint = endpoint}
  let private applier f = function DynamicProgram p -> DynamicProgram ((unbox f) p) | StaticProgram p -> StaticProgram ((unbox f) p)
  /// Defines a transformation of Elmish programs that changes the type of the client message
  let mapped (mapping:'client -> 'newclient) (mapper:Program<'model,'client,_> -> Program<'newmodel,'newclient,_> ) (clientProgram: ClientProgram<'model,'server,'originalclient,'client>)
    : ClientProgram<'newmodel,'server,'originalclient,'newclient> =
    {
        program = applier mapper clientProgram.program
        mapClientMsg = clientProgram.mapClientMsg >> mapping
        serverDispatch = clientProgram.serverDispatch
        subscribe = clientProgram.subscribe
        whenDown = clientProgram.whenDown
        endpoint = clientProgram.endpoint
        application = clientProgram.application
    }

  /// Defines a transformation of Elmish programs that don't change the type of the client message
  let simple mapper clientProgram =
    {
        program = applier mapper clientProgram.program
        mapClientMsg = clientProgram.mapClientMsg
        serverDispatch = clientProgram.serverDispatch
        subscribe = clientProgram.subscribe
        whenDown = clientProgram.whenDown
        endpoint = clientProgram.endpoint
        application = clientProgram.application
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

  let private run (program: ClientProgram<'model, 'server,'originalclient, 'client>) =

    let ws : ClientWebSocket option ref = ref None
    let mutable dispatch = None

    let subscribe model =
        let sub d =
            dispatch <- Some d
        let subscribe =
            match program.program with
            | StaticProgram p -> p.subscribe
            | DynamicProgram p -> p.subscribe
        sub ::
            subscribe model

    let converter = Fable.JsonConverter()
    let write o = JsonConvert.SerializeObject(o,converter)

    let rec websocket server r =
        lock ws (fun () ->
            match !r with
            | None ->
                let ws = new ClientWebSocket()
                r := Some ws
                ws.ConnectAsync(Uri(server), CancellationToken.None) |> Async.AwaitTask |> Async.Start
            | Some _ -> ())
    websocket program.endpoint ws

    let rec receiver buffer =
      let recBuffer = Array.zeroCreate 4096
      async {
            match !ws with
            |Some webs ->
                let! msg = webs.ReceiveAsync(ArraySegment(recBuffer),CancellationToken.None) |> Async.AwaitTask
                match msg.MessageType,recBuffer.[0..msg.Count-1],msg.EndOfMessage,msg.CloseStatus with
                |_,_,_,s when s.HasValue ->
                    do! webs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None) |> Async.AwaitTask
                    (webs :> IDisposable).Dispose()
                    ws := None
                | WebSocketMessageType.Text, data, complete, _ ->
                    let data = data::buffer
                    if complete then
                        let data = data |> List.rev |> Array.concat
                        let str = System.Text.Encoding.UTF8.GetString data
                        dispatch |> Option.iter (fun d ->
                            str |> JsInterop.ofJson |> program.mapClientMsg |> d)
                        return! receiver []
                    else
                        return! receiver data
                | _ -> return! receiver buffer
            | None ->
                websocket program.endpoint ws
                return! receiver []
        }

    let sender = MailboxProcessor.Start (fun mb ->
        let rec loop () = async {
            match !ws with
            | Some ws ->
              let! msg = mb.Receive()
              let arr = write msg |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
              do! ws.SendAsync(arr,WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
              return! loop()
            | None ->
              websocket program.endpoint ws
              return! loop() }
        loop ()
        )

    let onError(str,ex:Exception) =
        let onError =
            match program.program with
            | StaticProgram p -> p.onError
            | DynamicProgram p -> p.onError
        match ex with
        | :? ServerMsgException<'server> as ex -> sender.Post ex.Msg
        | ex -> onError(str,ex)

    program.serverDispatch := Some sender.Post
    let prunner =
        match program.program with
        | DynamicProgram p ->
            match program.application with
            | Some app ->
                let program =
                  { p with
                      subscribe = subscribe
                      onError = onError }
                DynamicRunner <| Program.runWithDynamicView app program
            | None -> failwith "Application was not defined"
        | StaticProgram p ->
            let program =
              { p with
                  subscribe = subscribe
                  onError = onError }
            StaticRunner <| Program.runWithStaticView program
    let ds = function
        | S msg -> sender.Post msg
        | C msg -> dispatch |> Option.iter (fun d -> msg |> program.mapClientMsg |> d)
    program.subscribe () |> List.iter (fun sub -> sub ds)
    prunner

  /// Creates the program loop with a websocket connection using dynamic views
  /// `app`: Xamarin `Application`
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkDynamicClient`
  let runDynamic app (program: ClientProgram<'model, 'server,'originalclient,'client>) =
    match program.program with
    | DynamicProgram _ ->
        match run { program with application = Some app } with
        |DynamicRunner r -> r
        |_ -> failwith "Static runner returned for dynamic program"
    |_ -> failwith "Dynamic runner used for static program"

  /// Creates the program loop with a websocket connection at the defined endpoint using dynamic views
  /// `server`: websocket endpoint
  /// `app`: Xamarin `Application`
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkDynamicClient`
  let runDynamicAt server app (program: ClientProgram<'model, 'server,'originalclient,'client>) =
    runDynamic app {program with endpoint = server}

  /// Creates the program loop with a websocket connection using dynamic views
  /// `app`: Xamarin `Application`
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkStaticClient`
  let runStatic (program: ClientProgram<'model, 'server,'originalclient,'client>) =
    match program.program with
    | StaticProgram _ ->
        match run program with
        |StaticRunner r -> r
        |_ -> failwith "Dynamic runner returned for static program"
    |_ -> failwith "Static runner used for dynamic program"

  /// Creates the program loop with a websocket connection at the defined endpoint using dynamic views
  /// `server`: websocket endpoint
  /// `app`: Xamarin `Application`
  /// `program`: A `ClientProgram` created with Elmish.Bridge's `Bridge.mkStaticClient`
  let runStaticAt server (program: ClientProgram<'model, 'server,'originalclient,'client>) =
    runStatic {program with endpoint = server}




[<AutoOpen>]
module CE =
    type ClientBuilder<'arg,'model,'server,'originalclient,'client> internal (init,update,view) =
        let zero : ClientProgram<'model,'server,'originalclient,'originalclient> =
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


    /// Creates the client using dynamic views. Run it with `Bridge.runDynamic`
    let dynamicBridge init update view = ClientBuilder(init, update, DynamicView view)

    /// Creates the client using static views. Run it with `Bridge.runStatic`
    let staticBridge init update view = ClientBuilder(init, update, StaticView (fun () -> let (p,b) = view () in (p :> Xamarin.Forms.Page),b))
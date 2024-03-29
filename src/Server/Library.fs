namespace Elmish.Bridge

open Elmish
open Newtonsoft.Json
open Fable.Remoting.Json

[<RequireQualifiedAccess>]
module Cmd =
        module OfAsyncWith =
            /// Command that will evaluate an async block to the message
            let result (start: Async<unit> -> unit)
                    (task: Async<'msg>) : Cmd<'msg> =
                let bind dispatch =
                    async {
                        let! r = task
                        dispatch r
                    }
                [bind >> start]

        module OfAsync =
            /// Command that will evaluate an async block to the message
            let inline result (task: Async<'msg>) : Cmd<'msg> =
                OfAsyncWith.result Cmd.OfAsync.start task

module internal Helpers =
    open FSharp.Reflection

    let converter = [| FableJsonConverter() :> JsonConverter |]

    let rpcBag =
        MailboxProcessor.Start(
            fun mb ->
              let rec loop mp =
                async {
                  match! mb.Receive() with
                  | Choice1Of2 ((gv:System.Guid,f:(string -> unit)),(ge,e)) ->
                       return!
                            mp
                            |> Map.add gv (f,ge)
                            |> Map.add ge (e,gv)
                            |> loop
                  | Choice2Of2 (g,s) ->
                       match mp |> Map.tryFind g with
                       | Some (f,e) ->
                            f s
                            return! mp |> Map.remove g |> Map.remove e |> loop
                       | None -> return! loop mp
                    }
              loop Map.empty)

    let ``|Is|_|``<'a> (o:obj) =
        match o with
        | :? 'a as v -> Some v
        | _ -> None

    let (|GUID|_|) (s:string) =
        match System.Guid.TryParse s with
        | true, g -> Some g
        | _ -> None

    let findGuids (json:string) : (System.Guid * System.Guid) list =
        seq {
            use sr = new System.IO.StringReader(json)
            use reader = new JsonTextReader(sr);
            while reader.Read() do
               yield
                match reader.TokenType, reader.Value with
                |t, null -> Choice1Of2 t
                |t,v -> Choice2Of2 (t,v)
        }
        |> Seq.windowed 6
        |> Seq.choose (function
            |[|Choice1Of2(JsonToken.StartObject)
               Choice2Of2(JsonToken.PropertyName, p1)
               Choice2Of2(JsonToken.String, Is(GUID v1))
               Choice2Of2(JsonToken.PropertyName, p2)
               Choice2Of2(JsonToken.String, Is(GUID v2))
               Choice1Of2(JsonToken.EndObject)|] ->
                    match p1,p2 with
                    | Is("ValueId"), Is("ExceptionId") -> Some(v1,v2)
                    | Is("ExceptionId"), Is("ValueId") -> Some (v2,v1)
                    |_ -> None
            | _ -> None)
        |> Seq.distinct
        |> Seq.toList



    let rec unroll (t: System.Type) =
        seq {
            if FSharpType.IsUnion t then
                yield!
                    FSharpType.GetUnionCases t
                    |> Seq.collect
                        (fun x ->
                            match x.GetFields() with
                            | [||] -> Seq.empty
                            | [| t1 |] ->
                                seq {
                                    yield
                                        t1.PropertyType.FullName,
                                        t1.PropertyType,
                                        fun j -> FSharpValue.MakeUnion(x, [| j |])

                                    yield!
                                        unroll t1.PropertyType
                                        |> Seq.map
                                            (fun (a, t, b) -> a, t, (fun j -> FSharpValue.MakeUnion(x, [| b j |])))
                                }
                            | tuple ->
                                let t =
                                    tuple
                                    |> Array.map (fun t -> t.PropertyType)
                                    |> FSharpType.MakeTupleType

                                Seq.singleton (
                                    t.FullName.Replace('+', '.'),
                                    t,
                                    fun j -> FSharpValue.MakeUnion(x, j |> FSharpValue.GetTupleFields)
                                ))
        }

    let tryFindType (name: string) mappings =
        match mappings |> Map.tryFind name with
        | Some x -> Some x
        | None ->
            let objTypes =
                name.Split([| '['; ']'; ',' |], System.StringSplitOptions.RemoveEmptyEntries)
                |> Array.filter (fun x -> not (x.StartsWith " "))

            mappings
            |> Map.tryPick
                (fun (k: string) v ->
                    let indexes =
                        objTypes
                        |> Array.map (fun x -> k.IndexOf x)
                        |> Array.filter (fun x -> x >= 0)

                    if indexes.Length = objTypes.Length
                       && (indexes
                           |> Array.sort
                           |> Array.compareWith Operators.compare indexes) = 0 then
                        Some v
                    else
                        None)


type internal ServerHubData<'model, 'server, 'client> =
    { Model: 'model
      ServerDispatch: Dispatch<'server>
      ClientDispatch: Dispatch<'client> }

/// Holds functions that will be used when interaction with the `ServerHub`
type ServerHubInstance<'model, 'server, 'client> =
    { Update: 'model -> unit
      Add: 'model -> Dispatch<'server> -> Dispatch<'client> -> unit
      Remove: unit -> unit }
    static member internal Empty : ServerHubInstance<'model, 'server, 'client> =
        { Add = fun _ _ _ -> ()
          Remove = ignore
          Update = ignore }

type internal ServerHubMessages<'model, 'server, 'client> =
    | ServerSendIf of ('model -> bool) * 'server
    | ClientSendIf of ('model -> bool) * 'client
    | GetModels of ('model list -> unit)
    | AddClient of System.Guid * ServerHubData<'model, 'server, 'client>
    | UpdateModel of System.Guid * 'model
    | DropClient of System.Guid
    | AddRPCCalback of value: (System.Guid * (string -> unit)) * exc: (System.Guid * (string -> unit))
    | CallCallback of System.Guid * string
    | SendMessages of (Dispatch<'client> * 'client)[]
    | AskAllClientsIf of predicate: ('model -> bool) *
        f: (unit -> 'client * System.Guid * System.Guid) *
        onValue: (Dispatch<'client> -> Dispatch<'server> -> string -> unit) *
        onException: (Dispatch<'client> -> Dispatch<'server> -> string -> unit)
    | Dummy

/// Holds the data of all connected clients
type ServerHub<'model, 'server, 'client>() =
    let mutable clientMappings =
        let t = typeof<'client>

        (if Reflection.FSharpType.IsUnion t then
             Helpers.unroll t
             |> Seq.map (fun (name, _, f) -> name, (fun (o: obj) -> f o :?> 'client))
             |> Map.ofSeq
         else
             Map.empty)
        |> Map.add t.FullName (fun (o: obj) -> o :?> 'client)

    let mutable serverMappings =
        let t = typeof<'server>

        (if Reflection.FSharpType.IsUnion t then
             Helpers.unroll t
             |> Seq.map (fun (name, _, f) -> name, (fun (o: obj) -> f o :?> 'server))
             |> Map.ofSeq
         else
             Map.empty)
        |> Map.add t.FullName (fun (o: obj) -> o :?> 'server)


    let init () = (Map.empty, Map.empty), Cmd.none

    let update action (data, callbacks) =
        match action with
        | Dummy -> (data, callbacks), Cmd.none
        | SendMessages messages ->
            (data, callbacks),
            Cmd.OfAsync.result
            <| async {
                messages
                |> Array.Parallel.iter (fun (d,m) -> d m)
                return Dummy }
        | AskAllClientsIf(predicate, msgGen, onValue, onExn ) ->
           let cbs, toSend =
                data
                |> Map.toArray
                |> Array.choose
                    (fun (_,{ Model = m; ServerDispatch = sd; ClientDispatch = cd }) ->
                        if predicate m then
                            let msg, vguid, eguid = msgGen()
                            ([|
                                vguid, onValue cd sd, eguid
                                eguid, onExn cd sd, vguid
                            |], (cd, msg)) |> Some
                        else None
                        )
                |> Array.unzip
                |> fun (c,ts) -> Array.concat c, ts
           (data, (callbacks, cbs) ||> Array.fold (fun m (g,f,og) -> m |> Map.add g (f,og))),
           SendMessages toSend |> Cmd.ofMsg
        | AddRPCCalback ((vguid,vfun), (eguid, efun)) ->
           (data,
            callbacks
            |> Map.add vguid (vfun, eguid)
            |> Map.add eguid (efun, vguid)), Cmd.none
        | CallCallback(guid, body) ->
            match callbacks |> Map.tryFind guid with
            | None ->  (data, callbacks), Cmd.none
            | Some (func, oguid) ->
                (data,
                    callbacks
                    |> Map.remove guid
                    |> Map.remove oguid),
                Cmd.OfAsync.result
                    <| async {
                        do func body
                        return Dummy}
        | ServerSendIf (predicate, msg) ->
            (data, callbacks),
            Cmd.OfAsync.result
            <| async {
                data
                |> Map.toArray
                |> Array.Parallel.iter (fun (_, { Model = m; ServerDispatch = d }) -> if predicate m then msg |> d)

                return Dummy
               }
        | ClientSendIf (predicate, msg) ->
            (data, callbacks),
            Cmd.OfAsync.result
            <| async {
                data
                |> Map.toArray
                |> Array.Parallel.iter (fun (_, { Model = m; ClientDispatch = d }) -> if predicate m then msg |> d)

                return Dummy
               }
        | GetModels ar ->
            (data, callbacks),
            Cmd.OfAsync.result
            <| async {
                data
                |> Map.toList
                |> List.map (fun (_, { Model = m }) -> m)
                |> ar

                return Dummy
               }
        | AddClient (guid, hd) -> (data |> Map.add guid hd, callbacks), Cmd.none
        | UpdateModel (guid, model) ->
           (data
            |> Map.tryFind guid
            |> Option.map (fun hd -> data |> Map.add guid { hd with Model = model })
            |> Option.defaultValue data, callbacks),
            Cmd.none
        | DropClient (guid) -> (data |> Map.remove guid, callbacks), Cmd.none


    let mb =
                MailboxProcessor.Start (fun mb ->
                    let rec loop dispatcher = async {
                        match! mb.Receive() with
                        | Choice1Of2 msg ->
                            dispatcher |> Option.iter(fun d -> d msg)
                            return! loop dispatcher
                        | Choice2Of2 dispatcher ->
                            return! loop (Some dispatcher)
                    }
                    loop None
                )

    let dispatcher = fun msg -> mb.Post (Choice1Of2 msg)

    do
        Program.mkProgram init update (fun _ _ _ -> ())
        |> Program.runWithDispatch
            (fun d ->
                mb.Post (Choice2Of2 d)
                dispatcher) ()


    /// Register the client mappings so inner messages can be transformed to the top-level `update` message
    member this.RegisterClient(map: 'Inner -> 'client) =
        clientMappings <-
            clientMappings
            |> Map.add typeof<'Inner>.FullName (fun (o: obj) -> o :?> 'Inner |> map)

        this


    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    member this.RegisterServer(map: 'Inner -> 'server) =
        serverMappings <-
            serverMappings
            |> Map.add typeof<'Inner>.FullName (fun (o: obj) -> o :?> 'Inner |> map)

        this

    abstract BroadcastClient : 'inner -> unit


    /// Send client message for all connected users
    default sh.BroadcastClient(msg: 'inner) =
        sh.SendClientIf (fun _ -> true) msg

    abstract BroadcastServer : 'inner -> unit

    /// Send server message for all connected users
    default sh.BroadcastServer(msg: 'inner) =
        sh.SendServerIf (fun _ -> true) msg

    abstract SendClientIf : ('model -> bool) -> 'inner -> unit

    /// Send client message for all connected users if their `model` passes the predicate
    default __.SendClientIf predicate (msg: 'inner) =
        clientMappings
        |> Helpers.tryFindType typeof<'inner>.FullName
        |> Option.iter
            (fun f ->
                (predicate, f msg)
                |> ClientSendIf
                |> dispatcher)

    abstract SendServerIf : ('model -> bool) -> 'inner -> unit

    /// Send server message for all connected users if their `model` passes the predicate
    default __.SendServerIf predicate (msg: 'inner) =
        serverMappings
        |> Helpers.tryFindType typeof<'inner>.FullName
        |> Option.iter
            (fun f ->
                (predicate, f msg)
                |> ServerSendIf
                |> dispatcher)

    abstract GetModels : unit -> 'model list

    /// Return the model of all connected users
    default __.GetModels() =
        Async.FromContinuations
            (fun (cont, _, _) -> dispatcher (GetModels cont))
        |> Async.RunSynchronously

    abstract AskClient: Dispatch<'client> -> (IReplyChannel<'T> -> 'client) -> Async<'T>

    /// Ask for a value for a specific client
    default __.AskClient clientDispatcher (f: IReplyChannel<'T> -> 'client) =
        Async.FromContinuations
            (fun (cont, econt, ccont) ->
                let guidValue = System.Guid.NewGuid()
                let guidExn = System.Guid.NewGuid()

                AddRPCCalback(
                        (guidValue, fun e -> Newtonsoft.Json.JsonConvert.DeserializeObject(e, typeof<'T>, Helpers.converter) :?> 'T |> cont),
                        (guidExn, fun e -> Newtonsoft.Json.JsonConvert.DeserializeObject(e, typeof<exn>, Helpers.converter) :?> exn |> econt))
                |> dispatcher
                clientDispatcher (f {ValueId = guidValue; ExceptionId = guidExn}))

    abstract AskAllClients:
        (IReplyChannel<'T> -> 'client) ->
        (Dispatch<'client> -> Dispatch<'server> -> 'T -> unit) ->
        (Dispatch<'client> -> Dispatch<'server> -> exn -> unit) ->
        unit

    /// Ask for a value for all clients and call function with result
    default sh.AskAllClients
        (f: IReplyChannel<'T> -> 'client)
        (onValue: Dispatch<'client> -> Dispatch<'server> -> 'T -> unit)
        (onException: Dispatch<'client> -> Dispatch<'server> -> exn -> unit) =
            sh.AskAllClientsIf (fun _ -> true) f onValue onException

    abstract AskAllClientsIf:
        ('model -> bool) ->
        (IReplyChannel<'T> -> 'client) ->
        (Dispatch<'client> -> Dispatch<'server> -> 'T -> unit) ->
        (Dispatch<'client> -> Dispatch<'server> -> exn -> unit) ->
        unit

    /// Ask for a value for all clients and call function with result if their `model` passes the predicate
    default __.AskAllClientsIf
        (predicate: 'model -> bool)
        (f: IReplyChannel<'T> -> 'client)
        (onValue: Dispatch<'client> -> Dispatch<'server> -> 'T -> unit)
        (onException: Dispatch<'client> -> Dispatch<'server> -> exn -> unit) =
            AskAllClientsIf(
                predicate,
                (fun () ->
                    let guidValue = System.Guid.NewGuid()
                    let guidExn = System.Guid.NewGuid()
                    f {ValueId = guidValue; ExceptionId = guidExn}, guidValue, guidExn),
                (fun s c e ->
                    let t = Newtonsoft.Json.JsonConvert.DeserializeObject(e, typeof<'T>, Helpers.converter) :?> 'T
                    onValue s c t),
                (fun s c e ->
                    let t = Newtonsoft.Json.JsonConvert.DeserializeObject(e, typeof<exn>, Helpers.converter) :?> exn
                    onException s c t)) |> dispatcher

    member private __.TreatReply(guid, body) =
        dispatcher (CallCallback(guid, body))

    static member internal TreatReply(sh: ServerHub<'model, 'server, 'client> option, guid, body) =
        sh
        |> Option.iter (fun sh -> sh.TreatReply(guid, body))

    member private __.Init() : ServerHubInstance<'model, 'server, 'client> =
        let guid = System.Guid.NewGuid()

        let add =
            fun model serverDispatch clientDispatch ->
                dispatcher
                    (
                        AddClient(
                            guid,
                            { Model = model
                              ServerDispatch = serverDispatch
                              ClientDispatch = clientDispatch }
                        )
                    )

        let remove =
            fun () -> dispatcher (DropClient guid)

        let update =
            fun model -> dispatcher (UpdateModel(guid, model))

        { Add = add
          Remove = remove
          Update = update }

    /// Used to create a default `ServerHubInstance` that does nothing when the `ServerHub` is not set
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    static member Initialize(sh: ServerHub<'model, 'server, 'client> option) =
        sh
        |> Option.map (fun sh -> sh.Init())
        |> Option.defaultValue ServerHubInstance.Empty

type BridgeDeserializer<'server> =
    | Text of (string -> 'server)
    | Binary of (byte [] -> 'server)





/// Defines server configuration
type BridgeServer<'arg, 'model, 'server, 'client, 'impl>(endpoint: string, init, update) =
    let emptyDispose = {new System.IDisposable with member _.Dispose() = ()}

    let mutable subscribe : 'model -> Sub<'server> = fun _ -> [[], fun _ -> emptyDispose]
    let mutable logMsg = ignore
    let mutable logRegister = ignore
    let mutable logSMsg = ignore
    let mutable logInit = ignore
    let mutable logModel = ignore

    let mutable mappings =
        let t = typeof<'server>

        if Reflection.FSharpType.IsUnion t then
            Helpers.unroll t
            |> Seq.map (fun (n, t, f) -> n.Replace('+', '.'), t, f)
            |> Seq.groupBy (fun (a, _, _) -> a)
            |> Seq.collect
                (fun (_, f) ->
                    match f |> Seq.tryItem 1 with
                    | None -> f |> Seq.map (fun (a, t, f) -> a, (t, f))
                    | Some _ -> Seq.empty)

            |> Map.ofSeq
            |> Map.add (t.FullName.Replace('+', '.')) (t, id)
            |> Map.map
                (fun _ (t, f) ->
                    Text
                        (fun (i: string) ->
                            Newtonsoft.Json.JsonConvert.DeserializeObject(i, t, Helpers.converter)
                            |> f
                            :?> 'server))

        else
            Map.empty


    let write (o: 'client) =
        Newtonsoft.Json.JsonConvert.SerializeObject(o, Helpers.converter)

    let mutable whenDown : 'server option = None
    let mutable serverHub : ServerHub<'model, 'server, 'client> option = None


    let read send dispatch str =
        logSMsg str

        let (name: string, o: string) =
            Newtonsoft.Json.JsonConvert.DeserializeObject(str, typeof<string * string>, Helpers.converter) :?> _

        match name.Split('|') with
        | [|"RC"; guid|] ->
            let g = System.Guid.Parse(guid)
            ServerHub.TreatReply(serverHub, g, o)
        | [|"RS"; name|] ->
            match Helpers.findGuids o with
            |[vguid,eguid] ->
                mappings
                |> Helpers.tryFindType (name.Replace('+', '.'))
                |> function
                   | None -> sprintf "E%O" eguid |> send
                   | Some e ->
                        e |>
                        function
                        | Text e -> e o
                        | Binary e -> e (System.Convert.FromBase64String o)
                        |> (fun o ->
                                Helpers.rpcBag.Post(
                                  Choice1Of2 (
                                    (vguid, sprintf "R%O%s" vguid >> send),
                                    (eguid, sprintf "R%O%s" eguid >> send)))
                                o)
                        |> dispatch
            | _ -> ()
        | [|name|] ->
            mappings
            |> Helpers.tryFindType (name.Replace('+', '.'))
            |> Option.iter (
                function
                | Text e -> e o
                | Binary e -> e (System.Convert.FromBase64String o)
                >> dispatch)
        | _ -> ()



    /// Server msg passed to the `update` function when the connection is closed
    member this.WithWhenDown n =
        whenDown <- Some n
        this

    /// Registers the `ServerHub` that will be used by this socket connections
    member this.WithServerHub sh =
        serverHub <- Some sh
        this

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    member this.Register<'Inner, 'server>(map: 'Inner -> 'server) =
        let t = typeof<'Inner>
        let name = t.FullName.Replace('+', '.')
        logRegister name

        mappings <-
            mappings
            |> Map.add
                name
                (Text
                    (fun (i: string) ->
                        Newtonsoft.Json.JsonConvert.DeserializeObject<'Inner>(i, Helpers.converter)
                        |> map))

        this
    /// Register the server mappings so inner messages can be transformed to the top-level `update` message using a custom deserializer
    member this.RegisterWithDeserializer<'Inner, 'server>(map: 'Inner -> 'server, deserializer) =
        let t = typeof<'Inner>
        let name = t.FullName.Replace('+', '.')
        logRegister name

        mappings <-
            mappings
            |> Map.add
                name
                (match deserializer with
                 | Text e -> Text(e >> map)
                 | Binary e -> Binary(e >> map))

        this
    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial model, but can dispatch new messages at any time.
    member this.WithSubscription sub =
        subscribe <- sub
        this

    /// Add a log function for the initial model
    member this.AddInitLogging log =
        let oldLogInit = logInit

        logInit <-
            fun m ->
                oldLogInit m
                log m

        this

    /// Add a log function for logging type names on registering
    member this.AddRegisterLogging log =
        let oldLogRegister = logRegister

        logRegister <-
            fun m ->
                oldLogRegister m
                log m

        this

    /// Add a log function after the model updating
    member this.AddModelLogging log =
        let oldLogModel = logModel

        logModel <-
            fun m ->
                oldLogModel m
                log m

        this

    /// Add a log function when receiving a new message
    member this.AddMsgLogging log =
        let oldLogMsg = logMsg

        logMsg <-
            fun m ->
                oldLogMsg m
                log m

        this

    /// Add a log function when receiving a raw socket message
    member this.AddSocketRawMsgLogging log =
        let oldLogSMsg = logSMsg

        logSMsg <-
            fun m ->
                oldLogSMsg m
                log m

        this

    /// Trace all the operation to the console
    member this.WithConsoleTracing =
        this
            .AddInitLogging(eprintfn "Initial state: %A")
            .AddMsgLogging(eprintfn "New message: %A")
            .AddSocketRawMsgLogging(eprintfn "Remote message: %s")
            .AddRegisterLogging(eprintfn "Type %s registered")
            .AddModelLogging(eprintfn "Updated state: %A")
    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member this.Start server (arg: 'arg) : 'impl =
        let inbox action =
            let clientDispatch = write >> action >> Async.Start
            let hubInstance = ServerHub.Initialize serverHub

            let innerInit clientDispatch arg =
                let model, cmds = init clientDispatch arg
                logInit model
                model, cmds |> Cmd.map Some

            let innerUpdate clientDispatch msg model =
                match msg with
                | Some m ->
                    logMsg m
                    let model, cmds = update clientDispatch m model
                    logModel model
                    model, cmds |> Cmd.map Some

                | None ->
                    whenDown
                    |> Option.iter
                        (fun msg ->
                            logMsg msg
                            let model, _ = update clientDispatch msg model
                            logModel model)

                    model, Cmd.ofEffect (fun _ -> hubInstance.Remove())

            let mb =
                MailboxProcessor.Start (fun mb ->
                    let rec loop dispatcher = async {
                        match! mb.Receive() with
                        | Choice1Of2 msg ->
                            dispatcher |> Option.iter(fun d -> d msg)
                            return! loop dispatcher
                        | Choice2Of2 dispatcher ->
                            return! loop (Some dispatcher)
                    }
                    loop None
                )

            let dispatch: Dispatch<'server option> = fun msg -> mb.Post (Choice1Of2 msg)

            Program.mkProgram (innerInit clientDispatch) (innerUpdate clientDispatch) (fun _ _ _ -> ())


            |> Program.withSetState (fun model _ -> hubInstance.Update model)
            |> Program.withSubscription
                (fun model ->
                    try
                        hubInstance.Add model (fun m -> dispatch (Some m)) clientDispatch
                        subscribe model |> List.map (fun (tag, g) -> tag, (fun dispatch -> Some >> dispatch) >> g)
                    with _ -> [[], fun _ -> emptyDispose])
            |> Program.runWithDispatch (fun d ->
                    mb.Post (Choice2Of2 d)
                    dispatch) arg

            read (action >> Async.Start) (Some >> dispatch),
            (fun () -> dispatch None)

        server endpoint inbox

[<RequireQualifiedAccess>]
module Bridge =
    /// Creates a `ServerBridge`
    /// Takes an `endpoint` where the server will listen for connections
    /// a `init` : `Dispatch<'client> -> 'arg -> 'model * Cmd<'server>`
    /// and a `update` : `Dispatch<'client> -> 'server -> 'model -> 'model * Cmd<'server>`
    /// Typical program, new commands are produced by `init` and `update` along with the new state.
    let mkServer
        endpoint
        (init: Dispatch<'client> -> 'arg -> ('model * Cmd<'server>))
        (update: Dispatch<'client> -> 'server -> 'model -> ('model * Cmd<'server>))
        =
        BridgeServer(endpoint, init, update)

    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial model, but can dispatch new messages at any time.
    let withSubscription subscribe (program: BridgeServer<_, _, _, _, _>) = program.WithSubscription subscribe

    /// Log changes on the model and received messages to the console
    let withConsoleTrace (program: BridgeServer<_, _, _, _, _>) = program.WithConsoleTracing

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    let register map (program: BridgeServer<_, _, _, _, _>) = program.Register map

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    let registerWithDeserializer map des (program: BridgeServer<_, _, _, _, _>) =
        program.RegisterWithDeserializer(map, des)

    /// Registers the `ServerHub` that will be used by this socket connections
    let withServerHub hub (program: BridgeServer<_, _, _, _, _>) = program.WithServerHub hub

    /// Server msg passed to the `update` function when the connection is closed
    let whenDown msg (program: BridgeServer<_, _, _, _, _>) = program.WithWhenDown msg

    /// Creates a websocket loop.
    /// `arg`: argument to pass to the `init` function.
    /// `program`: program created with `mkProgram`.
    let runWith server arg (program: BridgeServer<_, _, _, _, _>) = program.Start server arg

    /// Creates a websocket loop with `unit` for the `init` function.
    /// `program`: program created with `mkProgram`.
    let run server (program: BridgeServer<_, _, _, _, _>) = program.Start server ()

[<AutoOpen>]
module RPC =

  type RPC.IReplyChannel<'T> with

    member t.Reply(v:'T) =
       Helpers.rpcBag.Post(Choice2Of2(t.ValueId, Newtonsoft.Json.JsonConvert.SerializeObject(v, Helpers.converter)))

    member t.ReplyException(v:exn) =
       Helpers.rpcBag.Post(Choice2Of2(t.ExceptionId, Newtonsoft.Json.JsonConvert.SerializeObject(v, Helpers.converter)))

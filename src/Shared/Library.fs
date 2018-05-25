namespace Elmish.Remoting

open Elmish
/// Shared type. Separates which messages are processed on the client or the server
type Msg<'server,'client> =
    | S of 'server
    | C of 'client
/// Defines client configuration
type ClientProgram<'arg,'model,'server,'client,'view> = {
    program : Program<'arg,'model,Msg<'server,'client>,'view>
    onConnectionOpen : 'client option
    onConnectionLost : 'client option
  }
#if !FABLE_COMPILER
type internal ServerHubData<'model, 'server, 'client> = {
    Model : 'model
    Dispatch : Dispatch<Msg<'server,'client>>
}

/// Holds functions that will be used when interaction with the `ServerHub`
type ServerHubInstance<'model, 'server, 'client> = {
    Update : 'model -> unit
    Add : 'model -> Dispatch<Msg<'server, 'client>> -> unit
    Remove : unit -> unit
}

type internal ServerHubMessages<'model, 'server, 'originalclient, 'client> =
    | Broadcast of Msg<'server,'originalclient>
    | SendIf of ('model -> bool) *  Msg<'server,'originalclient>
    | GetModels of AsyncReplyChannel<'model list>
    | AddClient of System.Guid * ServerHubData<'model, 'server, 'client>
    | UpdateModel of System.Guid * 'model
    | DropClient of System.Guid
/// Holds the data of all connected clients
type ServerHub<'model, 'server, 'originalclient, 'client>
    (msgMap : Msg<'server,'originalclient> -> Msg<'server,'client>) =
    let mb =
        MailboxProcessor.Start(
           fun inbox ->
                let rec hub data =
                    async {
                        let! action = inbox.Receive()
                        match action with
                        | Broadcast msg ->                          
                          async {
                            data
                            |> Map.toArray                            
                            |> Array.Parallel.iter
                                (fun (_,{Dispatch = d}) -> msg |> msgMap |> d  ) } |> Async.Start
                        | SendIf(predicate, msg) ->
                          async {
                            data
                            |> Map.filter (fun _ {Model = m} -> predicate m )
                            |> Map.toArray
                            |> Array.Parallel.iter
                                (fun (_,{Dispatch = d}) -> msg |> msgMap |> d  ) } |> Async.Start
                        | GetModels ar ->
                          async {
                            data
                            |> Map.toList
                            |> List.map (fun (_,{Model=m})-> m )
                            |> ar.Reply } |> Async.Start
                        | AddClient(guid, hd) ->
                            return! hub (data |> Map.add guid hd)
                        | UpdateModel(guid,model) ->
                            let hd = data |> Map.tryFind guid
                            match hd with
                            |Some hd -> return! hub (data |> Map.add guid {hd with Model = model})
                            |None -> return! hub data
                        | DropClient(guid) ->
                            return! hub (data |> Map.remove guid)
                        return! hub data
                    }
                hub Map.empty)

    /// Creates a new `ServerHub`
    static member New() : ServerHub<'a,'b,'c,'c> = ServerHub(id)
    /// Send message for all connected users
    member __.Broadcast(msg) =
        mb.Post (Broadcast msg)
    /// Send message for all connected users if their `model` passes the predicate
    member __.SendIf =
        fun predicate msg ->
            mb.Post (SendIf (predicate,msg))
    /// Return the model of all connected users
    member __.GetModels() =
        mb.PostAndReply GetModels
    member private __.Init() : ServerHubInstance<'model, 'server, 'client> =
        let guid = System.Guid.NewGuid()

        let add =
            fun model dispatch ->
                mb.Post (AddClient (guid,{Model=model;Dispatch=dispatch}))
        let remove =
            fun () ->
                mb.Post (DropClient guid)
        let update =
            fun model ->
                mb.Post (UpdateModel (guid,model))

        {Add = add; Remove=remove; Update=update}
    /// Used to create a default `ServerHubInstance` that does nothing when the `ServerHub` is not set
    static member Initialize(sh:ServerHub<'model, 'server, 'originalclient, 'client> option)  =
        match sh with
        |None ->
          {
            Add = fun _ _ -> ()
            Remove = ignore
            Update = ignore}
        |Some sh -> sh.Init()

/// Defines server configuration
type ServerProgram<'arg, 'model, 'server, 'originalclient, 'client> = {
    init : 'arg -> 'model * Cmd<Msg<'server,'originalclient>>
    update : 'server -> 'model -> 'model * Cmd<Msg<'server,'originalclient>>
    mapMsg : Msg<'server, 'originalclient> -> Msg<'server, 'client>
    subscribe : 'model -> Cmd<Msg<'server,'originalclient>>
    serverHub : ServerHub<'model, 'server, 'originalclient, 'client> option
    onDisconnection : 'server option
}
#endif


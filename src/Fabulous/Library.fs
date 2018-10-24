namespace Elmish.Bridge

open System
open System.Net.WebSockets
open System.Threading
open Fabulous.Core
open Thoth.Json.Net

type BridgeConfig<'Msg,'ElmishMsg> =
    { path : string
      whenDown : 'ElmishMsg option
      mapping :  'Msg -> 'ElmishMsg
      retryTime : int
      name : string option}

[<RequireQualifiedAccess>]
module Bridge =
    let mutable mappings : Map<string option, string -> unit> = Map.empty
    /// Create a new `BridgeConfig` with the set endpoint
    let endpoint endpoint =
        {
            path = endpoint
            whenDown = None
            mapping = id
            retryTime = 1
            name = None
        }

    /// Set a message to be sent when connection is lost.
    let withWhenDown msg this =
        { this with whenDown = Some msg }

    /// Set a name for this bridge if you want to have a secondary one.
    let withName name this =
        { this with name = Some name }

    /// Configure how many seconds before reconnecting when the connection is lost.
    /// Values below 1 are ignored
    let withRetryTime sec this =
        if sec < 1 then
            this
        else
            { this with retryTime = sec}

    /// Configure a mapping to the top-level message so the server can send an inner message
    /// That enables using just a subset of the messages on the shared project
    let withMapping map this =
        {
            whenDown = this.whenDown
            path = this.path
            mapping = map
            retryTime = this.retryTime
            name = this.name
        }

    let Send (server : 'Server) =
        mappings
        |> Map.tryFind None
        |> Option.iter(fun o -> Thoth.Json.Net.Encode.Auto.toString (0,(typeof<'Server>.FullName.Replace('+','.'), server)) |> o )

    let NamedSend (name:string, server : 'Server) =
        mappings
        |> Map.tryFind (Some name)
        |> Option.iter(fun o -> Thoth.Json.Net.Encode.Auto.toString (0,(typeof<'Server>.FullName.Replace('+','.'), server)) |> o )

    let internal Attach (config:BridgeConfig<'Msg,'ElmishMsg>) (program : Program<_, 'ElmishMsg, _>) =
        let sub _ =
          let decoder : Decode.Decoder<'Msg> = Decode.Auto.generateDecoder false
          let dispatcher dispatch =
            let ws : ClientWebSocket option ref = ref None
            let rec websocket server r =
                lock ws (fun () ->
                    match !r with
                    | None ->
                        async {
                            let ws = new ClientWebSocket()
                            r := Some ws
                            match! ws.ConnectAsync(Uri(server), CancellationToken.None)
                                    |> Async.AwaitTask
                                    |> Async.Catch with
                            | Choice1Of2 () -> ()
                            | Choice2Of2 _ ->
                                r := None
                                config.whenDown |> Option.iter dispatch}
                        |> Async.StartImmediate                        
                    | Some _ -> ())
            websocket config.path ws
            let recBuffer = ArraySegment(Array.zeroCreate 4096)
            let rec receiver buffer =
              async {
                    match !ws with
                    |Some webs when webs.State = WebSocketState.Open ->
                        let! msg = webs.ReceiveAsync(recBuffer,CancellationToken.None) |> Async.AwaitTask
                        match msg.MessageType,recBuffer.Array.[0..msg.Count-1],msg.EndOfMessage,msg.CloseStatus with
                        |_,_,_,s when s.HasValue ->
                            do! webs.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None) |> Async.AwaitTask
                            (webs :> IDisposable).Dispose()
                            ws := None
                            config.whenDown |> Option.iter dispatch
                            do! Async.Sleep (1000 * config.retryTime)
                            return! receiver []
                        | WebSocketMessageType.Text, data, complete, _ ->
                            let data = data::buffer
                            if complete then
                                let data = data |> List.rev |> Array.concat
                                let str = System.Text.Encoding.UTF8.GetString data
                                let msg = Decode.fromString decoder str
                                match msg with
                                | Ok msg -> msg |> config.mapping |> dispatch
                                | Error er -> eprintfn "%s" er
                                return! receiver []
                            else
                                return! receiver data
                        | _ -> return! receiver buffer
                    | Some _ ->
                        do! Async.Sleep (1000 * config.retryTime)
                        return! receiver []
                    | None ->
                        websocket config.path ws
                        return! receiver []
                }
            Async.StartImmediate (receiver [])
            let sender = MailboxProcessor.Start (fun mb ->
                let rec loop () = async {
                    match !ws with
                    | Some ws ->
                      let! (msg : string) = mb.Receive()
                      let arr = msg |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
                      do! ws.SendAsync(arr,WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
                      return! loop()
                    | None ->
                      websocket config.path ws
                      return! loop() }
                loop ()
                )
            mappings <- mappings |> Map.add config.name sender.Post
          [dispatcher]
        Program.withSubscription sub program

[<RequireQualifiedAccess>]
module Program =
    open System.Diagnostics

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridge endpoint (program : Program<_, _, _>) =
        Bridge.Attach
            { path = endpoint
              whenDown = None
              mapping = id
              retryTime = 1
              name = None} program

    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    let withBridgeConfig (config:BridgeConfig<_,_>) (program : Program<_, _, _>) =
        Bridge.Attach config program

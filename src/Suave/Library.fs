namespace Elmish.Bridge

open System
[<RequireQualifiedAccess>]
module Suave =
    open Suave
    open Suave.Filters
    open Suave.Operators
    open Suave.Sockets
    open Suave.Sockets.Control
    open Suave.WebSocket
    /// Suave's server used by `ServerProgram.runServerAtWith` and `ServerProgram.runServerAt`
    /// Creates a `WebPart`
    let server (program: ServerProgram<'arg,'model,'server,'client,WebPart>) arg : WebPart=
        let ws (webSocket:WebSocket) _ =
            let hi = ServerHub.Initialize program.serverHub
            let inbox =
                Server.createMailbox
                    (fun s ->
                        let resp = s |> System.Text.Encoding.UTF8.GetBytes |> ByteSegment
                        webSocket.send Text resp true |> Async.Ignore)
                    hi arg program
            let skt =
              socket {
                let mutable loop = true
                let mutable buffer = []
                while loop do
                    let! msg = webSocket.read()
                    match msg with
                    |Text, data, complete ->
                        buffer <- data :: buffer
                        if complete then
                            let data = buffer |> List.rev |> Array.concat
                            let str = UTF8.toString data
                            let msg : 'server = Server.read str
                            (S msg) |> Server.Msg |> inbox.Post
                            buffer <- []
                    | (Close, _, _) ->
                        let emptyResponse = [||] |> ByteSegment
                        do! webSocket.send Close emptyResponse true
                        loop <- false
                    | _ -> ()}
            async {
                let! result = skt
                program.whenDown |> Option.iter (S >> Server.Msg >> inbox.Post)
                hi.Remove ()
                return result
            }
        path program.endpoint >=> handShake ws

[<AutoOpen>]
module CE =
    let bridge init update = ServerBuilder(Suave.server,init,update)
namespace Elmish.Remoting

[<RequireQualifiedAccess>]
module Suave =
    open Suave
    open Suave.Filters
    open Suave.Operators
    open Suave.Sockets
    open Suave.Sockets.Control
    open Suave.WebSocket
    let server uri arg (program: ServerProgram<_,_,_,_>) : WebPart=
        let ws (webSocket:WebSocket) _ =
            let inbox =
                Server.createMailbox
                    (fun s ->
                        let resp = s |> System.Text.Encoding.UTF8.GetBytes |> ByteSegment
                        webSocket.send Text resp true |> Async.Ignore)
                    arg program
            socket {
                let mutable loop = true
                while loop do
                    let! msg = webSocket.read()
                    match msg with
                    |Text, data, true ->
                        let str = UTF8.toString data
                        let msg : 'server = Server.read str
                        inbox.Post (S msg)
                    | (Close, _, _) ->
                        let emptyResponse = [||] |> ByteSegment
                        do! webSocket.send Close emptyResponse true
                        program.onDisconnection |> Option.iter (S>>inbox.Post)
                        loop <- false
                    | _ -> ()}
        path uri >=> handShake ws

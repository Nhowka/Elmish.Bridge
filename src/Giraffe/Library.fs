namespace Elmish.Remoting

[<RequireQualifiedAccess>]
module Giraffe =
    open System
    open Giraffe
    open FSharp.Control.Tasks.ContextInsensitive
    open System.Net.WebSockets
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Http
    open System.Threading
    /// Giraffe's server used by `ServerProgram.runServerAtWith` and `ServerProgram.runServerAt`
    /// Creates a `HttpHandler`
    let server uri arg (program: ServerProgram<'arg,'model,'server,'client>) : HttpHandler =
        let ws (next:HttpFunc) (ctx:HttpContext) =
          task {
            if ctx.WebSockets.IsWebSocketRequest then
                let hi = ServerHub.Initialize program.serverHub
                let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
                let inbox =
                    Server.createMailbox
                        (fun s ->
                            let resp = s |> System.Text.Encoding.UTF8.GetBytes |> ArraySegment
                            webSocket.SendAsync(resp,WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask)
                        hi arg program
                let skt =
                  task {
                    let buffer = Array.zeroCreate 4096
                    let mutable loop = true
                    while loop do
                        let! msg = webSocket.ReceiveAsync(ArraySegment(buffer), CancellationToken.None )
                        match msg.MessageType,buffer.[0..msg.Count-1],msg.EndOfMessage,msg.CloseStatus with
                        |_,_,_,s when s.HasValue ->
                            do! webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,CancellationToken.None)
                            loop <- false
                        |WebSocketMessageType.Text, data, true, _ ->
                            let str = System.Text.Encoding.UTF8.GetString data
                            let msg : 'server = Server.read str
                            (S msg) |> Server.Msg |> inbox.Post
                        | _ -> ()
                  }
                do! skt
                program.onDisconnection |> Option.iter (S >> Server.Msg >> inbox.Post)
                hi.Remove ()
                return Some ctx
            else
                return None
            }
        route uri >=> ws

    /// Prepare app to use websockets
    let useWebSockets (app:IApplicationBuilder) =
        app.UseWebSockets()
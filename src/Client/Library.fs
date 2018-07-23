namespace Elmish.Bridge
open Elmish
open Fable.Core
open Fable.Import
open Fable.Core.JsInterop
[<RequireQualifiedAccess>]
module internal Constants =
    [<Literal>]
    let internal dispatchIdentifier = "elmish_bridge_message_dispatch"
    [<Literal>]
    let internal socketIdentifier = "elmish_bridge_socket"


/// Creates the bridge. Takes the endpoint and an optional message to be dispatched when the connection is closed.
/// It exposes a method `Send` that can be used to send messages to the server
type BridgeConfig<'Msg> =
  {
      endpoint : string
      whenDown: 'Msg option
  }
  with
    [<PassGenerics>]
    member private this.Websocket<'Msg> server =
        let socket = Fable.Import.Browser.WebSocket.Create server
        Browser.window?(Constants.socketIdentifier) <- Some socket
        socket.onclose <- fun _ ->
            match this.whenDown with
            | Some m -> this.RaiseEvent (JsInterop.toJson m)
            |_ -> ()
            Fable.Import.Browser.window.setTimeout(this.Websocket server, 1000) |> ignore
        socket.onmessage <- fun e ->
            e.data |> string |> this.RaiseEvent

    [<PassGenerics>]
    member internal this.Attach(program:Elmish.Program<_,_,'Msg,_>) =
        let url = Fable.Import.Browser.URL.Create(Fable.Import.Browser.window.location.href)
        url.protocol <- url.protocol.Replace ("http","ws")
        url.pathname <- this.endpoint
        url.hash <- ""
        this.Websocket url.href

        let subs model =
            (fun dispatch -> Browser.window?(Constants.dispatchIdentifier) <- Some (JsInterop.ofJson<'Msg> >> dispatch))::program.subscribe model

        {program with subscribe = subs}

    [<PassGenerics>]
    member private __.RaiseEvent(msg:string) =
        !!Browser.window?(Constants.dispatchIdentifier) |>
            Option.iter(fun dispatch -> dispatch msg)

[<RequireQualifiedAccess>]
module Bridge =
    /// Send the message to the server
    [<PassGenerics>]
    let Send(server:'Server) =
        !!Browser.window?(Constants.socketIdentifier)
         |> Option.iter (fun (s:Fable.Import.Browser.WebSocket) ->
                                s.send(JsInterop.toJson (typeof<'Server>.FullName,server)))
[<RequireQualifiedAccess>]
module Program =
    /// Apply the `Bridge` to be used with the program.
    /// Preferably use it before any other operation that can change the type of the message passed to the `Program`.
    [<PassGenerics>]
    let withBridgeWhenDown endpoint whenDown (program:Program<_,_,'Msg,_>) =
        {
            endpoint = endpoint
            whenDown = Some whenDown }.Attach program
    [<PassGenerics>]
    let withBridge endpoint (program:Program<_,_,'Msg,_>) =
        {
            endpoint = endpoint
            whenDown = None }.Attach program
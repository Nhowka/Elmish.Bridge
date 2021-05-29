# Elmish.Bridge

Formely Elmish.Remoting. This library creates a bridge between server and client using websockets so you can keep the same model-view-update mindset to create the server side model.

## Available Packages:

| Library  | Version |
| -------- | ------- |
| Elmish.Bridge.Client  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Client.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Client) |
| Elmish.Bridge.Fabulous  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Fabulous.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Fabulous) |
| Elmish.Bridge.Suave  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Suave.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Suave)  |
| Elmish.Bridge.Giraffe  | [![Nuget](https://img.shields.io/nuget/v/Elmish.Bridge.Giraffe.svg?colorB=green)](https://www.nuget.org/packages/Elmish.Bridge.Giraffe)  |

## Why?

The MVU approach made programming client-side SPA really fun and fast for me. The idea of having a model updated in a predictable way, with a view just caring about what was in the model just clicked, and making a server-side with just consumable services made everything different and I felt like I was making two entirely different programs. I came up with this project so I can have the same fun when creating the server.

## Drawbacks

You are now just passing messages between the server and the client. Stuff like authorization, headers or cookies are not there anymore. You have a stateful connection between the client while it's open, but if it gets disconnected the server will lose all the state it had.
That can be mitigated by sending a message to the client when it reestabilishes the connection, so it knows that the server is there again. You can then send all the information that the server needs to feel like the connection was never broken in the first place.

tl;dr You have to relearn the server-side part

## How to use it?

This is Elmish on server with a bridge to the client. You can learn about Elmish [here](https://elmish.github.io/). This assumes that you know how to use Elmish on the client-side.

### Shared

I recommend to keep the messages and the endpoint on a shared file; you don't need to keep the model if you decide that you want the server and client to have different models.

```fsharp
// Messages processed on the server
type ServerMsg =
    |...
//Messages processed on the client
type ClientMsg =
    |...
module Shared =
    let endpoint = "/socket"
```

### Client

What's different? Well, now you can send messages to and get messages from the server. For it, just send the message using `Bridge.Send`. Simple as that. But before that, you need to enable the bridge on your Elmish `Program`, preferably before anything else so it can inject the incoming messages at the right place.

```fsharp
open Elmish
open Elmish.Bridge

Program.mkProgram init update view
|> Program.withBridge Shared.endpoint
|> Program.withReact "elmish-app"

```

**Warning: Because of a limitation on the reflection on Fable, you can't use primitives or generic types on the input of `Bridge.Send`/`Bridge.NamedSend`**

### Server

Now you can use the MVU approach on the server, minus the V. That still is just a client thing. For easy usage, the `init` and `update` function first argument is now a `Dispatch<ClientMsg>`. Just call that function with the client message and it will be sent there.

The server has a little more configuration to do, but that enables the client to be very simple to use while being compatible with the vanilla Elmish. A problem that exists is that the client can send any kind of message, not just the expected message that the `update` listens to. Because of that, you need register every type that you want to be listening and how to get to the original top-level message.

**As of version 3.0, you won't need to register every mapping to the top-level type. The server will register every type in the union that has no ambiguity. For this example, you won't need to register them anymore as `FirstInnerMsg` is only mapped by `First` and `SecondInnerMsg` is only mapped by `Second`.**

Imagine that you have the following model:

```fsharp
type FirstInnerMsg =
  | FIA
  | FIB
type SecondInnerMsg =
  | SIA
  | SIB
type OuterMsg = // That's the one the update expects
  | SomeMsg
  | First of FirstInnerMsg
  | Second of SecondInnerMsg
```

`OuterMsg` is registered by default, but to enable the client to do `Bridge.Send FIA` or `Bridge.Send SIB` all you need to do is:

```fsharp
let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.register First // First is a function (FirstInnerMsg -> OuterMsg)
  |> Bridge.register Second // Any ('a -> OuterMsg) will work
  |> Bridge.run (* server implementation here*)
```

As for the server implementation, there is one for Suave on the `Elmish.Bridge.Suave` package and another for Giraffe/Saturn on the `Elmish.Bridge.Giraffe` package.

- Suave

```fsharp
open Elmish
open Elmish.Bridge

let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.run Suave.server

let webPart =
  choose [
    server
    Filters.path "/" >=> Files.browseFileHome "index.html"
  ]
startWebServer config webPart
```

- Giraffe

```fsharp
open Elmish
open Elmish.Bridge

let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.run Giraffe.server

let webApp =
  choose [
    server
    route "/" >=> htmlFile "/pages/index.html" ]

let configureApp (app : IApplicationBuilder) =
  app
    .UseWebSockets()
    .UseGiraffe webApp

let configureServices (services : IServiceCollection) =
  services.AddGiraffe() |> ignore

WebHostBuilder()
  .UseKestrel()
  .Configure(Action<IApplicationBuilder> configureApp)
  .ConfigureServices(configureServices)
  .Build()
  .Run()
```

- Saturn

```fsharp
open Elmish
open Elmish.Bridge

let server =
  Bridge.mkServer Shared.endpoint init update
  |> Bridge.run Giraffe.server

let webApp =
  choose [
    server
    route "/" >=> htmlFile "/pages/index.html" ]

let app =
  application {
    use_router webApp
    disable_diagnostics
    app_config Giraffe.useWebSockets
    url uri
  }

run app
```

### ServerHub

WebSockets are even better when you use that communication power to send messages between clients. No library would be complete without help to broadcast messages or send a message to an specific client. That's where the `ServerHub` comes in! It is a very simple class that worries about keeping the information about all the connected clients so you don't have to.

Create a new `ServerHub` and register all the mappings that you will use by using:

```fsharp
let hub =
  ServerHub()
    .RegisterServer(First)
    .RegisterServer(Second)
    .RegisterClient(ClientMsg)

```

then you can use it on your server:

```fsharp
Bridge.mkServer Shared.endpoint init update
|> Bridge.withServerHub hub
|> Bridge.run server
```

It has three functions:
* `Broadcast(Server/Client)`

    Send a message to anyone connected:
    ```fsharp
      hub.BroadcastClient (NewMessage "Hello, everyone!")
    ```

* `Send(Server/Client)If`

    Send a message to anyone whose model satisfies a predicate.
    ```fsharp
      hub.SendClientIf (function {Gender = Female} -> true | _ -> false) (NewMessage "Hello, ladies!")
    ```

* `GetModels`

    Gets a list with all models of all connected clients.
    ```fsharp
      let users = hub.GetModels() |> List.map (function {Name = n} -> n)
      hub.BroadcastClient (ConnectedUsers users)
    ```

These functions were enough when creating a simple chat, but let me know if you feel limited having only them!

## Other configuration

Besides `Program.withBridge`, there is `Program.withBridgeConfig` that can configure some aspects:

- As the endpoint is mandatory, create the `BridgeConfig` using `Bridge.endpoint`

- If you need that endpoint to be relative instead of absolute, chain the config with `Bridge.withUrlMode`. More on that later.

- For defining a time in seconds to reconnect, chain the config with `Bridge.withRetryTime`

- For defining a message to be sent to the client when the connection is lost, chain the config with `Bridge.withWhenDown`

- For defining a name for your bridge so you can use more than one, chain the config with `Bridge.withName`. More on that later.

- For defining a mapping so the server can send a different message to the client, chain the config with `Bridge.withMapping`. More on that on the next section.

- (Since 3.0) For defining a custom serializer so you aren't limited on sending JSON data through the socket, pass a custom serializer returning `SerializerResult` to `withCustomSerializer`. More on that later.

- (Since 3.4) If you need to get some data before initializing the connection or you only want to enable it if your user does some action, like entering a chat page, you can pass the `BridgeConfig` to `Bridge.asSubscription` to get something that can be passed to `Cmd.ofSub`.

- (Since 5.0) `BridgeConfig` now implements `IDisposable`, closing the connection when `Dispose()` is called. That is useful for using it with some React hooks that close resources when detaching the component.

## Minimizing shared messages

You can share just a sub-set of the messages between client and server. The message type used by the first argument of `init` and `update` functions on the server is what will be sent, so on the client you can add a mapping from that type for the type used on the client's `init`/`update`.

Imagine you have the following model:

```fsharp
type BridgeAware =
  | Hello of string

type SomeOtherMessage =
  | InnerBridge of BridgeAware
  | ...

type TopLevel =
  | SomeMsg of SomeOtherMessage
  | ...

```

You can make the server functions like this:

```fsharp
let init (clientDispatch:Dispatch<BridgeAware>) () =
  clientDispatch (Hello "I came from the server!")
  someModel,someCmds
```

And configure the client like this:

```fsharp
Program.mkProgram init update view
|> Program.withBridgeConfig(
    Bridge.endpoint Shared.endpoint
    |> Bridge.withMapping
        (fun bridgeMsg ->
            bridgeMsg
            |> InnerBridge
            |> SomeMsg))
|> Program.withReact "elmish-app"
```

That way, only the `BridgeAware` type needs to be on the shared file

## Configuring the endpoint

Sometimes you may need that path to be relative with the current URL. Maybe you want to define an external endpoint. For that, you can pass the following cases to `Bridge.withUrlMode`:

- `Replace`: the default. Uses the current host and replaces the path with the endpoint defined.
- `Append`: Appends the endpoint defined to the current URL.
- `Raw`: Uses the defined path as a complete URL.
- `Calculated`: Takes an extra function `(string -> string -> string)`. The functions arguments are the current URL and the endpoint defined, returning the resulting URL.

As Fabulous runs outside the browser the endpoint is assumed to be raw. Pay attention to the protocol and use `ws://` or `wss://` to connect.

## Webpack caveat

When using the development mode of Webpack, usually a proxy is defined so the server calls can be redirected to the right place. That proxy doesn't work for websockets by default. To enable them, use `ws: true` when configuring the endpoint.

Example:

```
devServer: {
    proxy: {
      '/api/*': {
        target: 'http://localhost:' + port,
        changeOrigin: true
      },
      '/socket': {
        target: 'http://localhost:' + port,
        ws: true
     }
    },
    contentBase: "./public",
    hot: true,
    inline: true
  }
```

## Named bridges

Sometimes you have more than one feature where the bridge can be useful. You can have a real-time notification when the user's order is approved and also have a chat so it can talk to support. These features have nothing in common, so you don't need to clutter your logic with all remote stuff you do.

When using a name, you can use the method `Bridge.NamedSend` that takes a name (defined with `Bridge.withName`) and the desired message tupled. There's a annoying behavior that prevents it to be curried and partially applied, but here is an workaround:

```fsharp
open Fable.Core
let inline chatMessage x = Bridge.NamedSend("Chat", x)
let inline notification x = Bridge.NamedSend("Notification", x)
```

then you can use it on your `update`:

```fsharp
  ...
  match msg with
  | ClientSentMessage msg ->
      chatMessage (NewMessage msg)
  ...
```

Don't forget to use the same name on the `BridgeConfig`:

```fsharp
Program.mkProgram init update view
|> Program.withBridgeConfig
    (Bridge.endpoint "/socket/chat"
    |> Bridge.withName "Chat"
    |> Bridge.withMapping ChatMessages)
|> Program.withBridgeConfig
    (Bridge.endpoint "/socket/notification"
    |> Bridge.withName "Notification"
    |> Bridge.withMapping NotificationMessages)
|> ...
```

## Custom serialization (since 3.0)

If you want to use a different serialization for sending a type for the server, you can register a custom serialization that takes the desired input and maps it into a `SerializerResult`:

```fsharp
/// SerializerResult is defined as:
type SerializerResult =
    | Text of string
    | Binary of byte []

/// You can serialize a simple type like
type Action =
    | Increment
    | Decrement

/// Using a serializer with the signature (Action -> SerializerResult) like

let actionSerializer = function
    | Increment -> Text "+"
    | Decrement -> Text "-"

/// And then register it on the BridgeConfig
Program.mkProgram init update view
|> Program.withBridgeConfig
    (Bridge.endpoint "/socket/chat"
    |> Bridge.withCustomSerializer actionSerializer)
|> ...

/// As for the server, you need to use the BridgeDeserializer
type BridgeDeserializer<'server> =
    | Text of (string -> 'server)
    | Binary of (byte[] -> 'server)

/// For Action, that would be
let deserializer = Text (function "+" -> Increment | "-" -> Decrement)

/// Suppose your top level message is defined as that
type ServerMessage =
    | TheAction of Action
    | AnotherMessage

/// Then you can use it after registering it
Bridge.mkServer Shared.endpoint init update
|> Bridge.registerWithDeserializer TheAction deserializer
|> Bridge.run (...)
```

## Elmish Cmd (since 3.1.1)

As an extension to the `Cmd` module, you can now use `Cmd.bridgeSend` and `Cmd.namedBridgeSend` to create a `Cmd` on your `update` and `init` functions. That is more alike other libraries that extends Elmish.

```fsharp
let init () =
    None, Cmd.bridgeSend (GiveMeTheModel)
```

By default, if you send a message while you are disconnected, nothing happens. If you want to dispatch a message to the loop in that case, you can instead use `Cmd.bridgeSendOr` or `Cmd.namedBridgeSendOr` to define a fallback message.

```fsharp
let init () =
    None, Cmd.bridgeSendOr (WhatsTheAnswer) (ItIs 42)
```

## Reply channels (since 5.0.0)

If before you used some Bridge just to get some data from the server or vice versa, you probably used a message to encode the _asking_ and another to encode the _answering_. Unless you had some sort of identifier for the round-trip, you'd have a bad time knowing which message came from each question. Reply channels aims to help on that specific case, but feel free to get creative!

For the shared part, now both `Elmish.Bridge.Client` and `Elmish.Bridge.Server` has a dependency on `Elmish.Bridge.RPC`. This package has a single record, `IReplyChannel<'T>`. It is inspired by the `AsyncReplyChannel<'Reply>` used on the `MailboxProcessor` when you call `PostAndReply`.

Suppose that before you had a:

```fsharp
type Server =
    | AskQuery of QueryParameters

type Client =
    | QueryAnswer of QueryResult
```

You could abstract that to a single message:

```fsharp
open Elmish.Bridge

type Server =
    | Query of QueryParameters * IReplyChannel<QueryResult>
```

Client code that before needed to use elmish messages

```fsharp
let update msg model =
    match msg with
    | DoQuery queryParameter ->
        model, Cmd.bridgeSend (AskQuery queryParameter)
    | Remote (QueryAnswer result) ->
        // do something with the result
```

can be refactored to instead be on `async` expressions:

```fsharp
let doQuery queryParameter =
  async {
    let! result = Bridge.AskServer(fun rc -> AskQuery(queryParameter, rc))
    /// do something with the result
  }
```


The client also has a `Bridge.AskNamedServer` for named Bridges.

For the side supposed to answer on the reply channel, the change is not that big:

```fsharp
let update clientDispatch msg model =
    match msg with
    | Remote(AskQuery queryParameter) ->
        let result = doQuery queryParameter
        clientDispatch (QueryAnswer result)
        model, Cmd.none
```

would turn into:

```fsharp
let update clientDispatch msg model =
    match msg with
    | Remote(AskQuery (queryParameter, replyChannel)) ->
        let result = doQuery queryParameter
        replyChannel.Reply result
        model, Cmd.none
```

The server can also ask the clients for values. The `ServerHub` was extended with the methods `

- `AskClient`
  - takes
    - a `Dispatch<'client>`: same as the one on `update`
    - an `IReplyChannel<'T> -> 'client`: to build the client message
  - returns
    - an `Async<'T>`: with the client's response
- `AskAllClients`
  - takes
    - an `IReplyChannel<'T> -> 'client`: to build the client message
    - an `Dispatch<'client> -> Dispatch<'server> -> 'T -> unit`: for processing the successful messages. You can use the client dispatch to send new messages to the client and use the server dispatch to add new messages to the server's `update`
    - an `Dispatch<'client> -> Dispatch<'server> -> exn -> unit`: same as above, but for exceptions
  - returns `unit`
- `AskAllClientsIf`
   - takes
     - a `model -> bool`: filters so only clients that has something on their models receive the message
     - same as `AskAllClients`

## Anything more?

This documentation is on an early stage, if you have any questions feel free to open an issue or PR so we can have it in a good shape. You can check a test project [here](https://github.com/Nhowka/BridgeChat). I hope you enjoy using it!

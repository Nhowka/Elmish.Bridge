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
/// Defines server configuration
type ServerProgram<'arg, 'model, 'server, 'client> = {
    init : 'arg -> 'model * Cmd<Msg<'server,'client>>
    update : 'server -> 'model -> 'model * Cmd<Msg<'server,'client>>
    subscribe : 'model -> Cmd<Msg<'server,'client>>
    onDisconnection : 'server option
}



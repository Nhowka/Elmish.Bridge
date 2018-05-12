namespace Elmish.Remoting

open Elmish

type Msg<'server,'client> =
    | S of 'server
    | C of 'client

type ClientProgram<'arg,'model,'server,'client,'view> = {
    program : Program<'arg,'model,Msg<'server,'client>,'view>
    onConnectionOpen : 'client option
    onConnectionLost : 'client option
  }

type ServerProgram<'arg, 'model, 'server, 'client> = {
    init : 'arg -> 'model * Cmd<Msg<'server,'client>>
    update : 'server -> 'model -> 'model * Cmd<Msg<'server,'client>>
    subscribe : 'model -> Cmd<Msg<'server,'client>>
    onDisconnection : 'server option
}



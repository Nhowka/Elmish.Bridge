namespace Elmish.Remoting.HMR
open Elmish.HMR
open Elmish
open Elmish.Remoting
[<RequireQualifiedAccess>]
module ClientProgram =
  let fromHMRProgram (program:Program<'arg,Program.HMRModel<'model>,Program.HMRMsg<Msg<'server,'client>>,'view>) :
      ClientProgram<'arg,Program.HMRModel<'model>,'server,Program.HMRMsg<'client>,'view> =
      let mapM = function
        |Program.Reload -> C Program.Reload
        |Program.UserMsg (C a) -> C (Program.UserMsg a)
        |Program.UserMsg (S a) -> S a
      let mapT (model,cmd) = model, Cmd.map mapM cmd
      let updateT ms md =
        let ms =
          match ms with
          |S a -> Program.UserMsg(S a)
          |C (Program.UserMsg a) -> Program.UserMsg(C a)
          |C (Program.Reload) -> Program.Reload
        program.update ms md |> mapT
      {
      program =
        {
          init = program.init >> mapT
          setState = fun m d -> program.setState m (mapM >> d)
          subscribe = fun m -> program.subscribe m |> Cmd.map mapM
          update = updateT
          view = fun m d -> program.view m (mapM >> d)
          onError = program.onError
           }
      onConnectionOpen = None
      onConnectionLost = None}

[<RequireQualifiedAccess>]
module ServerProgram =
  let withHMR (program:ServerProgram<_,_,_,_>) =
    let mapM = function
      | S a -> S a
      | C a -> C (Program.UserMsg a)

    let map (model,cmd) =
      model, cmd |> Cmd.map mapM
    {
      init = fun m -> program.init m |> map
      update = fun ms md -> program.update ms md |> map
      subscribe = program.subscribe >> (Cmd.map mapM)
      onDisconnection = program.onDisconnection
    }
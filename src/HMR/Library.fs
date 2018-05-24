namespace Elmish.Remoting.HMR
open Elmish.HMR
open Elmish
open Elmish.Remoting
[<RequireQualifiedAccess>]
module Helpers =
  /// Maps a `'client` msg to a `HMRMsg<'client>`
  let mapMsg = function
    | S a -> S a
    | C a -> C (Program.UserMsg a)
#if !FABLE_COMPILER    
  /// Creates a `ServerHub` that supports `HMRMsg<'client>`
  let newServerHub() = ServerHub(mapMsg)
#endif
[<RequireQualifiedAccess>]
module ClientProgram =
  /// Maps the `'client` message to a `HMRMsg<'client>` message
  let toHMRMsg = Program.UserMsg
  /// Creates a `ClientProgram` when the Elmish `Program` uses the HMR module
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
#if !FABLE_COMPILER    
[<RequireQualifiedAccess>]
module ServerProgram =
  /// Send a `HMRMsg<'client>` instead of a `'client` message.
  /// Used when the client is using the HMR module.
  /// Note: When using a `ServerHub` you must set it after calling this method
  let withHMR (program:ServerProgram<'arg,'model,'server,'client,'client>) =
    {
      init = program.init
      mapMsg = Helpers.mapMsg
      update = program.update
      subscribe = program.subscribe
      onDisconnection = program.onDisconnection
      serverHub = None
    }
#endif
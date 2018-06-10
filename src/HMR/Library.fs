namespace Elmish.Remoting.HMR
open Elmish.HMR
open Elmish
[<RequireQualifiedAccess>]
module RemoteProgram =
  /// Maps the `'client` message to a `HMRMsg<'client>` message
  let HMRMsgMapping = Program.UserMsg

  let HMRModelMapping model : Program.HMRModel<_> = {HMRCount = 0;UserModel = model}

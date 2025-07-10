/// API endpoint to receive and handle an Op
module ApiServer.AddOps

open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.AspNetCore.Http

open Prelude
open Tablecloth
open Http

module Telemetry = LibService.Telemetry
module Span = Telemetry.Span

module C = LibBackend.Canvas
module Serialize = LibBackend.Serialize
module Op = LibBackend.Op
module PT = LibExecution.ProgramTypes
module AT = LibExecution.AnalysisTypes
module CTApi = ClientTypes.Api
module CT2Ops = ClientTypes2BackendTypes.Ops
module LD = LibService.LaunchDarkly

// Toplevel deletion:
// * The server announces that a toplevel is deleted by it appearing in
// * deleted_toplevels. The server announces it is no longer deleted by it
// * appearing in toplevels again.

module V1 =
  let causesAnyChanges (ops : PT.Oplist) : bool = List.any Op.hasEffect ops

  /// API endpoint to add a set of Op in a Canvas
  ///
  /// The Ops usually relate to a single Toplevel within the Canvas,
  /// but can technically include Ops against several TLIDs
  let addOp (ctx : HttpContext) : Task<CTApi.Ops.AddOpV1.Response> =
    task {
      use t = startTimer "read-api" ctx
      let canvasInfo = loadCanvasInfo ctx

      // We're winding down Darklang-Classic
      // This snippet short-circuits ApiServer to fail on requests to inactive canvases
      //   , while brownouts are active.
      let! canvasShouldBeKeptActive = C.shouldCanvasBeKeptActive canvasInfo.id
      if LD.brownoutIsActive () && (not canvasShouldBeKeptActive) then
        raise (
          HttpStatusException(
            410,
            "Darklang-Classic is winding down: https://blog.darklang.com/winding-down-darklang-classic. "
            + "Your canvas has not been marked to be kept active - "
            + "please reach out at classic@darklang.com if you'd like to keep your canvas active during the brownout period and beyond."
          )
        )

      let! p = ctx.ReadVanillaJsonAsync<CTApi.Ops.AddOpV1.Request>()
      let p = CT2Ops.AddOpParamsV1.fromCT p
      let canvasID = canvasInfo.id

      let! isLatest =
        Serialize.isLatestOpRequest (Some p.clientOpCtrID) p.opCtr canvasInfo.id

      let newOps = p.ops
      let newOps = if isLatest then newOps else Op.filterOpsReceivedOutOfOrder newOps
      let opTLIDs = List.map Op.tlidOf newOps
      Telemetry.addTags [ "opCtr", p.opCtr
                          "clientOpCtrID", p.clientOpCtrID
                          "opTLIDs", opTLIDs ]

      t.next "load-saved-ops"
      let! dbTLIDs =
        match Op.requiredContextToValidateOplist newOps with
        | Op.NoContext -> Task.FromResult []
        // NOTE: Because we run canvas-wide validation logic, it's important that
        // we load _at least_ the context (ie. datastores, functions, types, etc)
        // and not just the tlids in the API payload.
        | Op.AllDatastores -> Serialize.fetchTLIDsForAllDBs canvasInfo.id

      let allTLIDs = opTLIDs @ dbTLIDs
      // We're going to save this, so we need all the ops
      let! oldOps =
        Serialize.loadOplists
          Serialize.IncludeDeletedToplevels
          canvasInfo.id
          allTLIDs
      let oldOps = oldOps |> List.map Tuple2.second |> List.concat

      let c = C.fromOplist canvasInfo oldOps newOps

      t.next "to-frontend"

      let result : Op.AddOpResultV1 =
        { handlers = Map.values c.handlers
          deletedHandlers = Map.values c.deletedHandlers
          dbs = Map.values c.dbs
          deletedDBs = Map.values c.deletedDBs
          userFunctions = Map.values c.userFunctions
          deletedUserFunctions = Map.values c.deletedUserFunctions
          userTypes = Map.values c.userTypes
          deletedUserTypes = Map.values c.deletedUserTypes }

      t.next "save-to-disk"
      // work out the result before we save it, in case it has a
      // stackoverflow or other crashing bug
      if causesAnyChanges newOps then
        do!
          (oldOps @ newOps)
          |> Op.oplist2TLIDOplists
          |> List.filterMap (fun (tlid, oplists) ->
            let tlPair =
              match Map.get tlid (C.toplevels c) with
              | Some tl -> Some(tl, C.NotDeleted)
              | None ->
                match Map.get tlid (C.deletedToplevels c) with
                | Some tl -> Some(tl, C.Deleted)
                | None ->
                  Telemetry.addEvent "Undone handler" [ "tlid", tlid ]
                  // If we don't find anything, this was Undo-ed completely. Let's not
                  // do anything.
                  // https://github.com/darklang/dark/issues/3675 for discussion.
                  None
            Option.map (fun (tl, deleted) -> (tlid, oplists, tl, deleted)) tlPair)
          |> C.saveTLIDs canvasInfo


      t.next "send-ops-to-pusher"
      // To make this work with prodclone, we might want to have it specify
      // more ... else people's prodclones will stomp on each other ...
      if causesAnyChanges newOps then
        LibBackend.Pusher.push
          ClientTypes2BackendTypes.Pusher.eventSerializer
          canvasID
          (LibBackend.Pusher.AddOpV1(p, result))
          //(Some <| LibBackend.Pusher.AddOpPayloadTooBig(List.map Op.tlidOf p.ops))
          None // AddOpPayloadTooBig is so-far unsupported by the client

      t.next "send-event-to-heapio"
      // NB: I believe we only send one op at a time, but the type is op list
      newOps
      // MoveTL and TLSavepoint make for noisy data, so exclude it from heapio
      |> List.filter (fun op ->
        match op with
        | PT.MoveTL _
        | PT.TLSavepoint _ -> false
        | _ -> true)
      |> List.iter (fun op ->
        LibService.HeapAnalytics.track
          canvasInfo.id
          canvasInfo.name
          canvasInfo.owner
          (Op.eventNameOfOp op)
          Map.empty)

      return CT2Ops.AddOpResultV1.toCT result
    }

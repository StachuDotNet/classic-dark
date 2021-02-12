module LibBackend.UserDB

// Anything relating to Datastores on a user canvas

open System.Threading.Tasks
open FSharp.Control.Tasks
open Npgsql.FSharp.Tasks
open Npgsql
open Db

open Prelude
open Tablecloth

module PT = ProgramSerialization.ProgramTypes
module RT = LibExecution.RuntimeTypes

// Bump this if you make a breaking change to the underlying data format, and
// are migrating user data to the new version
//
// ! you should definitely notify the entire engineering team about this
let currentDarkVersion = 0

type Uuid = System.Guid

// -------------------------
// actual DB stuff *)
// -------------------------
let typeErrorMsg
  (colName : string)
  (expected : RT.DType)
  (actual : RT.Dval)
  : string =
  let expected = LibExecution.DvalRepr.typeToDeveloperReprV0 expected
  let actual = LibExecution.DvalRepr.prettyTypename actual
  $"Expected a value of type {expected} but got a {actual} in column {colName}"



let rec query_exact_fields state (db : RT.DB.T) query_obj : (string * RT.Dval) list =
  fstodo "query_exact_fields"
//   let sql =
//     "SELECT key, data
//      FROM user_data
//      WHERE table_tlid = $1
//      AND user_version = $2
//      AND dark_version = $3
//      AND canvas_id = $4
//      AND data @> $5"
//   in
//   Db.fetch
//     ~name:"fetch_by"
//     sql
//     ~params:
//       [ ID db.tlid
//       ; Int db.version
//       ; Int current_dark_version
//       ; Uuid state.canvas_id
//       ; QueryableDval query_obj ]
//   |> List.map ~f:(fun return_val ->
//          match return_val with
//          (* TODO(ian): change `to_obj` to just take a string *)
//          | [key; data] ->
//              (key, to_obj db [data])
//          | _ ->
//              Exception.internal "bad format received in fetch_all")
//
//
// and
//     (* PG returns lists of strings. This converts them to types using the
//  * row info provided *)
//     to_obj db (db_strings : string list) : dval =
//   match db_strings with
//   | [obj] ->
//       let p_obj =
//         match Dval.of_internal_queryable_v1 obj with
//         | DObj o ->
//             (* <HACK>: some legacy objects were allowed to be saved with `id` keys _in_ the
//          * data object itself. they got in the datastore on the `update` of
//          * an already present object as `update` did not remove the magic `id` field
//          * which had been injected on fetch. we need to remove magic `id` if we fetch them
//          * otherwise they will not type check on the way out any more and will not work.
//          * if they are re-saved with `update` they will have their ids removed.
//          * we consider an `id` key on the map to be a "magic" one if it is present in the map
//          * but not in the schema of the object. this is a deliberate weakening of our schema
//          * checker to deal with this case. *)
//             if not
//                  (List.exists
//                     ~f:(( = ) "id")
//                     (db |> cols_for |> List.map ~f:Tuple.T2.get1))
//             then Map.remove o "id"
//             else o
//         (* </HACK> *)
//         | x ->
//             Exception.internal ("failed format, expected DObj got: " ^ obj)
//       in
//       (* <HACK>: because it's hard to migrate at the moment, we need to
//      * have default values when someone adds a col. We can remove this
//      * when the migrations work properly. Structured like this so that
//      * hopefully we only have to remove this small part. *)
//       let default_keys =
//         cols_for db
//         |> List.map ~f:(fun (k, _) -> (k, DNull))
//         |> DvalMap.from_list_exn
//       in
//       let merged = Util.merge_left p_obj default_keys in
//       (* </HACK> *)
//       let type_checked = type_check db merged in
//       DObj type_checked
//   | _ ->
//       Exception.internal "Got bad format from db fetch"


// TODO: Unify with Type_checker.ml
and typeCheck (db : RT.DB.T) (obj : RT.DvalMap) : RT.DvalMap =
  let cols = Map.ofList db.cols in
  let tipeKeys = cols |> Map.keys |> Set.ofList in
  let objKeys = obj |> Map.keys |> Set.ofList in
  let sameKeys = tipeKeys = objKeys in

  if sameKeys then
    Map.mapWithIndex
      (fun key value ->
        match (Map.get key cols |> Option.unwrapUnsafe, value) with
        | RT.TInt, RT.DInt _ -> value
        | RT.TFloat, RT.DFloat _ -> value
        | RT.TStr, RT.DStr _ -> value
        | RT.TBool, RT.DBool _ -> value
        | RT.TDate, RT.DDate _ -> value
        | RT.TList _, RT.DList _ -> value
        // FSTODO
        // | RT.TDbList _, RT.DList _ ->
        //     value
        // FSTODO
        // | RT.TPassword, RT.DPassword _ ->
        //     value
        | RT.TUuid, RT.DUuid _ -> value
        | RT.TDict _, RT.DObj _ -> value
        | RT.TRecord _, RT.DObj _ -> value
        | _, RT.DNull -> value (* allow nulls for now *)
        | expectedType, valueOfActualType ->
            // FSTODO can be shown to users
            failwith (typeErrorMsg key expectedType valueOfActualType))
      obj
  else
    let missingKeys = Set.difference tipeKeys objKeys in

    let missingMsg =
      "Expected but did not find: ["
      + (missingKeys |> Set.toList |> String.concat ", ")
      + "]"

    let extraKeys = Set.difference objKeys tipeKeys in

    let extraMsg =
      "Found but did not expect: ["
      + (extraKeys |> Set.toList |> String.concat ", ")
      + "]"

    match (Set.isEmpty missingKeys, Set.isEmpty extraKeys) with
    | false, false -> failwith $"{missingMsg} & {extraMsg}"
    | false, true -> failwith missingMsg
    | true, false -> failwith extraMsg
    | true, true ->
        failwith
          "Type checker error! Deduced expected and actual did not unify, but could not find any examples!"


and set
  (state : RT.ExecutionState)
  (upsert : bool)
  (db : RT.DB.T)
  (key : string)
  (vals : RT.DvalMap)
  : Task<Uuid> =
  let id = System.Guid.NewGuid()
  let merged = typeCheck db vals

  let upsertQuery =
    if upsert then
      "ON CONFLICT ON CONSTRAINT user_data_key_uniq DO UPDATE SET data = EXCLUDED.data"
    else
      ""

  Sql.query
    $"INSERT INTO user_data
       (id, account_id, canvas_id, table_tlid, user_version, dark_version, key, data)
       VALUES (@id, @accountID, @canvasID, @tlid, @userVersion, @darkVersion, @key, @data)
       {upsertQuery}"
  |> Sql.parameters [ "id", Sql.uuid id
                      "accountID", Sql.uuid state.accountID
                      "canvasID", Sql.uuid state.canvasID
                      "tlid", Sql.id db.tlid
                      "userVersion", Sql.int db.version
                      "darkVersion", Sql.int currentDarkVersion
                      "key", Sql.string key
                      "data", Sql.queryableDvalMap merged ]
  |> Sql.executeStatementAsync
  |> Task.map (fun () -> id)



// and get_option ~state (db : RT.DB.T) (key : string) : dval option =
//   Db.fetch_one_option
//     ~name:"get"
//     "SELECT data
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5
//      AND key = $6"
//     ~params:
//       [ ID db.tlid
//       ; Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; Int db.version
//       ; Int current_dark_version
//       ; String key ]
//   |> Option.map ~f:(to_obj db)
//
//
// and get_many ~state (db : RT.DB.T) (keys : string list) : (string * dval) list =
//   Db.fetch
//     ~name:"get_many"
//     "SELECT key, data
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5
//      AND key = ANY (string_to_array($6, $7)::text[])"
//     ~params:
//       [ ID db.tlid
//       ; Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; Int db.version
//       ; Int current_dark_version
//       ; List (List.map ~f:(fun s -> String s) keys)
//       ; String Db.array_separator ]
//   |> List.map ~f:(fun return_val ->
//          match return_val with
//          (* TODO(ian): change `to_obj` to just take a string *)
//          | [key; data] ->
//              (key, to_obj db [data])
//          | _ ->
//              Exception.internal "bad format received in get_many")
//
//
// and get_many_with_keys ~state (db : RT.DB.T) (keys : string list) :
//     (string * dval) list =
//   Db.fetch
//     ~name:"get_many_with_keys"
//     "SELECT key, data
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5
//      AND key = ANY (string_to_array($6, $7)::text[])"
//     ~params:
//       [ ID db.tlid
//       ; Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; Int db.version
//       ; Int current_dark_version
//       ; List (List.map ~f:(fun s -> String s) keys)
//       ; String Db.array_separator ]
//   |> List.map ~f:(fun return_val ->
//          match return_val with
//          (* TODO(ian): change `to_obj` to just take a string *)
//          | [key; data] ->
//              (key, to_obj db [data])
//          | _ ->
//              Exception.internal "bad format received in get_many_with_keys")
//
//
// let get_all ~state (db : RT.DB.T) : (string * dval) list =
//   Db.fetch
//     ~name:"get_all"
//     "SELECT key, data
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5"
//     ~params:
//       [ ID db.tlid
//       ; Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; Int db.version
//       ; Int current_dark_version ]
//   |> List.map ~f:(fun return_val ->
//          match return_val with
//          (* TODO(ian): change `to_obj` to just take a string *)
//          | [key; data] ->
//              (key, to_obj db [data])
//          | _ ->
//              Exception.internal "bad format received in get_all")
//
//
// let get_db_fields (db : RT.DB.T) : (string * tipe) list =
//   List.filter_map db.cols ~f:(function
//       | Filled (_, field), Filled (_, tipe) ->
//           Some (field, tipe)
//       | _ ->
//           None)
//
//
// let query ~state (db : RT.DB.T) (b : RT.DB.Tlock_args) : (string * dval) list =
//   let db_fields = Tablecloth.StrDict.from_list (get_db_fields db) in
//   let param_name =
//     match b.params with
//     | [(_, name)] ->
//         name
//     | _ ->
//         Exception.internal "wrong number of args"
//   in
//   let sql =
//     Sql_compiler.compile_lambda ~state b.symtable param_name db_fields b.body
//   in
//   let result =
//     try
//       Db.fetch
//         ~name:"filter"
//         ( "SELECT key, data
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5
//      AND ("
//         ^ sql
//         ^ ")" )
//         ~params:
//           [ ID db.tlid
//           ; Uuid state.account_id
//           ; Uuid state.canvas_id
//           ; Int db.version
//           ; Int current_dark_version ]
//     with e ->
//       Libcommon.Log.erroR "error compiling sql" ~data:(Exception.to_string e) ;
//       raise (DBQueryException "A type error occurred at run-time")
//   in
//   result
//   |> List.map ~f:(fun return_val ->
//          match return_val with
//          (* TODO(ian): change `to_obj` to just take a string *)
//          | [key; data] ->
//              (key, to_obj db [data])
//          | _ ->
//              Exception.internal "bad format received in get_all")
//
//
// let query_count ~state (db : RT.DB.T) (b : RT.DB.Tlock_args) : int =
//   let db_fields = Tablecloth.StrDict.from_list (get_db_fields db) in
//   let param_name =
//     match b.params with
//     | [(_, name)] ->
//         name
//     | _ ->
//         Exception.internal "wrong number of args"
//   in
//   let sql =
//     Sql_compiler.compile_lambda ~state b.symtable param_name db_fields b.body
//   in
//   let result =
//     try
//       Db.fetch
//         ~name:"filter"
//         ( "SELECT COUNT(*)
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5
//      AND ("
//         ^ sql
//         ^ ")" )
//         ~params:
//           [ ID db.tlid
//           ; Uuid state.account_id
//           ; Uuid state.canvas_id
//           ; Int db.version
//           ; Int current_dark_version ]
//     with e ->
//       Libcommon.Log.erroR "error compiling sql" ~data:(Exception.to_string e) ;
//       raise (DBQueryException "A type error occurred at run-time")
//   in
//   result |> List.hd_exn |> List.hd_exn |> int_of_string
//
//
// let get_all_keys ~state (db : RT.DB.T) : string list =
//   Db.fetch
//     ~name:"get_all_keys"
//     "SELECT key
//       FROM user_data
//       WHERE table_tlid = $1
//       AND account_id = $2
//       AND canvas_id = $3
//       AND user_version = $4
//       AND dark_version = $5"
//     ~params:
//       [ ID db.tlid
//       ; Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; Int db.version
//       ; Int current_dark_version ]
//   |> List.map ~f:(fun return_val ->
//          match return_val with
//          | [key] ->
//              key
//          | _ ->
//              Exception.internal "bad format received in get_all_keys")
//
//
// let count ~state (db : RT.DB.T) : int =
//   Db.fetch
//     ~name:"count"
//     "SELECT count(*)
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5"
//     ~params:
//       [ ID db.tlid
//       ; Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; Int db.version
//       ; Int current_dark_version ]
//   |> List.hd_exn
//   |> List.hd_exn
//   |> int_of_string
//
//
// let delete ~state (db : RT.DB.T) (key : string) =
//   (* covered by composite PK index *)
//   Db.run
//     ~name:"user_delete"
//     "DELETE FROM user_data
//      WHERE key = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND table_tlid = $4
//      AND user_version = $5
//      AND dark_version = $6"
//     ~params:
//       [ String key
//       ; Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; ID db.tlid
//       ; Int db.version
//       ; Int current_dark_version ]
//
//
// let delete_all ~state (db : RT.DB.T) =
//   (* covered by idx_user_data_current_data_for_tlid *)
//   Db.run
//     ~name:"user_delete_all"
//     "DELETE FROM user_data
//      WHERE account_id = $1
//      AND canvas_id = $2
//      AND table_tlid = $3
//      AND user_version = $4
//      AND dark_version = $5"
//     ~params:
//       [ Uuid state.account_id
//       ; Uuid state.canvas_id
//       ; ID db.tlid
//       ; Int db.version
//       ; Int current_dark_version ]


// -------------------------
// stats/locked/unlocked (not _locking_)
// -------------------------
// let stats_pluck ~account_id ~canvas_id (db : RT.DB.T) : (dval * string) option =
//   let latest =
//     Db.fetch
//       ~name:"stats_pluck"
//       "SELECT data, key
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5
//      ORDER BY created_at DESC
//      LIMIT 1"
//       ~params:
//         [ ID db.tlid
//         ; Uuid account_id
//         ; Uuid canvas_id
//         ; Int db.version
//         ; Int current_dark_version ]
//     |> List.hd
//   in
//   match latest with
//   | Some [data; key] ->
//       Some (to_obj db [data], key)
//   | _ ->
//       None
//
//
// let stats_count ~account_id ~canvas_id (db : RT.DB.T) : int =
//   Db.fetch
//     ~name:"stats_count"
//     "SELECT count(*)
//      FROM user_data
//      WHERE table_tlid = $1
//      AND account_id = $2
//      AND canvas_id = $3
//      AND user_version = $4
//      AND dark_version = $5"
//     ~params:
//       [ ID db.tlid
//       ; Uuid account_id
//       ; Uuid canvas_id
//       ; Int db.version
//       ; Int current_dark_version ]
//   |> List.hd_exn
//   |> List.hd_exn
//   |> int_of_string


// Given a [canvasID] and an [accountID], return tlids for all unlocked databases -
// a database is unlocked if it has no records, and thus its schema can be
// changed without a migration.
//
// [ownerID] is needed here because we'll use it in the DB JOIN; we could
// pass in a whole canvas and get [canvasID] and [accountID] from that, but
// that would require loading the canvas, which is undesirable for performance
// reasons
let unlocked (ownerID : UserID) (canvasID : CanvasID) : Task<List<tlid>> =
  // this will need to be fixed when we allow migrations
  // Note: tl.module IS NULL means it's a db; anything else will be
  // HTTP/REPL/CRON/WORKER or a legacy space
  // NOTE: the line `AND tl.account_id = ud.account_id` seems redunant, but
  // it's required to hit the index
  Sql.query
    "SELECT tl.tlid
     FROM toplevel_oplists as tl
     LEFT JOIN user_data as ud
            ON tl.tlid = ud.table_tlid
           AND tl.canvas_id = ud.canvas_id
           AND tl.account_id = ud.account_id
     WHERE tl.canvas_id = @canvasID
       AND tl.account_id = @accountID
       AND tl.module IS NULL
       AND tl.deleted = false
       AND ud.table_tlid IS NULL
     GROUP BY tl.tlid"
  |> Sql.parameters [ "canvasID", Sql.uuid canvasID; "accountID", Sql.uuid ownerID ]
  |> Sql.executeAsync (fun read -> read.tlid "tlid")


// -------------------------
// DB schema
// -------------------------

let create (tlid : tlid) (name : string) (pos : pos) : PT.DB.T =
  { tlid = tlid; pos = pos; name = name; nameID = gid (); cols = []; version = 0 }


let create2 (tlid : tlid) (name : string) (pos : pos) (nameID : id) : PT.DB.T =
  { tlid = tlid; name = name; nameID = nameID; pos = pos; cols = []; version = 0 }

let renameDB (n : string) (db : PT.DB.T) : PT.DB.T = { db with name = n }

let addCol colid typeid (db : PT.DB.T) : PT.DB.T =
  { db with
      cols = db.cols @ [ { name = ""; typ = None; nameID = colid; typeID = typeid } ] }

let setColName id name (db : PT.DB.T) : PT.DB.T =
  let set (col : PT.DB.Col) =
    if col.nameID = id then { col with name = name } else col

  { db with cols = List.map set db.cols }

let setColType (id : id) (typ : PT.DType) (db : PT.DB.T) =
  let set (col : PT.DB.Col) =
    if col.typeID = id then { col with typ = Some typ } else col

  { db with cols = List.map set db.cols }

let deleteCol id (db : PT.DB.T) =
  { db with
      cols = List.filter (fun col -> col.nameID <> id && col.typeID <> id) db.cols }


// let create_migration rbid rfid cols (db : PT.DB.T) =
//   match db.active_migration with
//   | Some migration ->
//       db
//   | None ->
//       let max_version =
//         db.old_migrations
//         |> List.map ~f:(fun m -> m.version)
//         |> List.fold_left ~init:0 ~f:max
//       in
//       { db with
//         active_migration =
//           Some
//             { starting_version = db.version
//             ; version = max_version + 1
//             ; cols
//             ; state = DBMigrationInitialized
//             ; rollback = Libshared.FluidExpression.EBlank rbid
//             ; rollforward = Libshared.FluidExpression.EBlank rfid } }
//
//
// let add_col_to_migration nameid typeid (db : PT.DB.T) =
//   match db.active_migration with
//   | None ->
//       db
//   | Some migration ->
//       let mutated_migration =
//         {migration with cols = migration.cols @ [(Blank nameid, Blank typeid)]}
//       in
//       {db with active_migration = Some mutated_migration}
//
//
// let set_col_name_in_migration id name (db : PT.DB.T) =
//   match db.active_migration with
//   | None ->
//       db
//   | Some migration ->
//       let set col =
//         match col with
//         | Blank hid, tipe when hid = id ->
//             (Filled (hid, name), tipe)
//         | Filled (nameid, oldname), tipe when nameid = id ->
//             (Filled (nameid, name), tipe)
//         | _ ->
//             col
//       in
//       let newcols = List.map ~f:set migration.cols in
//       let mutated_migration = {migration with cols = newcols} in
//       {db with active_migration = Some mutated_migration}
//
//
// let set_col_type_in_migration id tipe (db : RT.DB.T) =
//   match db.active_migration with
//   | None ->
//       db
//   | Some migration ->
//       let set col =
//         match col with
//         | name, Blank blankid when blankid = id ->
//             (name, Filled (blankid, tipe))
//         | name, Filled (tipeid, oldtipe) when tipeid = id ->
//             (name, Filled (tipeid, tipe))
//         | _ ->
//             col
//       in
//       let newcols = List.map ~f:set migration.cols in
//       let mutated_migration = {migration with cols = newcols} in
//       {db with active_migration = Some mutated_migration}
//
//
// let abandon_migration (db : RT.DB.T) =
//   match db.active_migration with
//   | None ->
//       db
//   | Some migration ->
//       let mutated_migration = {migration with state = DBMigrationAbandoned} in
//       let db2 =
//         {db with old_migrations = db.old_migrations @ [mutated_migration]}
//       in
//       {db2 with active_migration = None}
//
//
// let delete_col_in_migration id (db : PT.DB.T) =
//   match db.active_migration with
//   | None ->
//       db
//   | Some migration ->
//       let newcols =
//         List.filter migration.cols ~f:(fun col ->
//             match col with
//             | Blank nid, _ when nid = id ->
//                 false
//             | Filled (nid, _), _ when nid = id ->
//                 false
//             | _ ->
//                 true)
//       in
//       let mutated_migration = {migration with cols = newcols} in
//       {db with active_migration = Some mutated_migration}
//

let placeholder = 0

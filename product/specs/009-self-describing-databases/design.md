# 009 — Self-describing databases: design

## 1. The identity record

Box 0, space `0x07`, a single key:

| Key | Value |
|---|---|
| `[0][0x07]` (5 bytes) | the identity record (JSON, UTF-8): `{ "v": 1, "kind": "database", "uid": "…", "created": 638…}` |

- `uid` is the canonical 36-character GUID form (`DatabaseRecord.NewUid()`, `003` design § 8.1). `created` is UTC ticks,
  as `DatabaseRecord.CreatedTicks` already is. `kind` is `database` or `registry`.
- JSON, like the box and database records, so it can grow by version. Unknown fields are ignored within version 1;
  a field this build needs and does not find is damage.
- New type `Storage/DatabaseIdentity.cs`: `internal sealed record DatabaseIdentity(string Uid, DatabaseKind Kind,
  long CreatedTicks)` with `Serialize`/`Deserialize`, and `internal enum DatabaseKind { Database, Registry }`.
- `BoxKeys.IdentityKey = [0x00, 0x00, 0x00, 0x00, 0x07]`, beside `FormatKey` and `EncoderVersionKey`.

## 2. The catalogue

`Storage/DatabaseContext.cs`:

- `CatalogueFormat` stays 1 (R1.3).
- `InitializeNewCatalogue(DatabaseIdentity identity)` writes the identity in the same synced batch as the format,
  encoder version, allocator and generation.
- `LoadCatalogue(DatabaseIdentity expected)` replaces `LoadCatalogue()`:
  1. Reads every box-0 key as now, adding `case 0x07 when key.Length == 5`.
  2. Checks the format and encoder version as today.
  3. **The identity must be present and parse** (R1.2, R1.3).
  4. **Its UID and kind must equal `expected`'s** (R2.3, R3.1). Otherwise it throws `CrystalFormatException` with:
     - `foundFormat: "database {found uid}"`;
     - `supportedFormat: "database {expected uid}"`;
     - a way forward: "The folder db-{id} holds another database. Put the right folder back, or drop this database if
       its data is not needed."
  5. Returns the identity read, so the caller can correct the registry's `created` (R2.3).
- `expected.CreatedTicks` is never compared; only UID and kind are (R2.3).
- A `null` UID in `expected` means "any UID, check the kind only". Only `_crystal` uses it (§ 3).

## 3. The store

`Storage/StoreEngine.cs`:

| Where | Change |
|---|---|
| new store (line ~232) | `_crystal`'s catalogue initialised with `new DatabaseIdentity(NewUid(), Registry, now)` |
| open (line ~269) | `_crystal` loaded with an expected identity of kind `registry` and **any** UID: the registry's own UID is recorded nowhere else, so for `_crystal` only kind is checked |
| create (line ~596) | the new database's identity is built from the record being created: its UID, kind `database`, its `CreatedTicks` |
| open a database (line ~707) | `LoadCatalogue(expected from the registry record)`; if the returned `CreatedTicks` differs from the record's, write the record back with the database's value: one synced registry batch, **no generation change**, under the store gate, logged at `Debug` |

`Storage/StandbyDatabase.cs:65` passes the expected identity from the registry record, as above. This is what makes
R5.1 hold. `003` T10, when built, installs the snapshot and then opens it through this path.

`LoadCatalogue` taking an `expected` parameter means **no caller can open a catalogue without saying which database
it expects**.

## 4. Events

`DatabaseCreatedTimeCorrected = 1310` (`Debug`): the registry's creation time was set to the database's (R2.3).
The refusals are logged where every `CrystalFormatException` on open already is.

## 5. Errors

All refusals are `CrystalFormatException` (existing type), each with its way forward:

| Case | Way forward |
|---|---|
| UID mismatch | put the right folder back, or drop the database |
| a registry where a database is expected, or the reverse | the folder was moved by hand; put it back |
| identity missing, unparsable, no version | restore the database from a backup, or drop it |
| identity version this build does not read | open with a newer Crystal build |

## 6. Layout changes

`engineering/box-layout.md`:

- **§ 4 table:** a row `[0][0x07]`: the identity record: UID, kind, creation time; written once at create, never
  changed. "Spaces `0x07`–`0xFF` reserved" becomes "`0x08`–`0xFF`".
- **§ 4 rules:** a new rule: "A database describes itself. Its format, encoder version and identity are inside it; its
  id and name belong to the store that holds it, and nothing inside it records them, so a database moves between
  stores whole."
- **§ 2:** the `_crystal/` line states what the registry holds, per `009` R2.1. It drops "replicated first, so a
  standby learns the database set from it", which `003` R1.4 already replaced with set reconciliation.

`005` design: a dated addendum A10 pointing to this spec. `003` design § 2: a dated note that the UID's source is the
database's identity record, and the registry keeps a copy.

## 7. What this design deliberately does not do

- **No upgrade of existing databases** (R4.1): there are none.
- **No placeholder database on a standby** (R5.2, corrected): a new database arrives whole by snapshot, which brings the
  primary's identity, and is registered with it.
- **No change to `DatabaseRecord`,** the alias table, the allocator, the generation, or any operation's ordering.
- **No new public API.**

## 8. Risks

- **The registry's `created` correction** is a registry write on an ordinary open. It happens at most once per
  database, only where a copy was wrong, and never moves the generation. A test shows a second open writes nothing.
- **Local stores from before this spec** stop opening (R4.1). That is intended; they are reset.

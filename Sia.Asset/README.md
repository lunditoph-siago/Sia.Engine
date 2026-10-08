# Asset mechanisms

Sia.Asset retains the original AssetLibrary, ECS asset entities, generated asset
construction, record references, dependency edges, lifetimes and loaders. It
provides generic mechanisms; callers compose domain loading and resource use.
It has no Mesh, scene, shader, font, rendering-quality or GPU workflow policy.

## Identity and acquisition

AssetId is a nonempty logical Guid, independent of content hashes/build keys.
Authors keep it stable across revisions. AssetRefer<TRecord>.Id resolves an
already acquired asset in a World; a reference alone does not load or pin data.
Existing Record/Entity/Name/Matcher references retain their behavior.

Attach AssetLibrary and register the concrete generated/manual asset type before
acquisition. AcquireAsset<TRecord>(id, factory) uses the original record/entity
construction path. The synchronous factory runs only on a miss and must return
the registered concrete record type. A hit preserves the existing value and
lifetime. Wrong-kind IDs, empty IDs, null results and same-ID recursion fail.
An anonymous record can receive one identity; it cannot receive a second ID.

The World overload without a referrer defaults to Persistent. The overload with
a referrer defaults to Automatic and adds the original dependency edge. That
referrer must be a live asset entity in the same World. Destroying an owner
releases its edges; Automatic children survive while another referrer remains.
Persistent assets require explicit release. Detaching the library clears its
indexes, preserving World-owned entities; reattaching rebuilds the indexes.

All acquisition and graph mutation run on the owning World's execution context.
Complete asynchronous preparation before acquisition. Construction failure
publishes no identity; asset construction precedes entity allocation. This does
not provide rollback for arbitrary caller/listener side effects, live revision
replacement, thread-safe World access or a multi-asset transaction.

## Bounded content preparation

ILoadable<TRecord> and ILoadable<TRecord,TOptions> remain concrete codec contracts.
FileLoader/EmbeddedLoader own and close the streams they open. A concrete decoder
borrows its stream; a record that needs data after loading must own that data.

AssetContent<TRecord> describes one logical ID and immutable ordered chunks of
its encoded file, plus an optional decoder name. It owns no payload/entity and
performs no catalog lookup. Chunk order and repetitions contribute file bytes;
chunk dependency metadata is not an implicit request to load other assets.

ChunkLoader.LoadAsync composes the existing AssetChunkCache with ILoadable. It
acquires/copies/releases each byte lease, then decodes from an owned temporary
contiguous stream that is closed on success or failure. The default encoded
input limit is 128 MiB per load; concrete decoder options specify separate
admission rules. Cache, encoded input, decoded data and concurrent/process memory
are different budgets. Byte requests coalesce through the existing cache; decoded
records do not. A started synchronous decoder completes despite late cancellation.

The caller explicitly connects the prepared record to original acquisition:

```csharp
// IO/decoding: no World mutation. TRecord implements the concrete ILoadable contract.
var prepared = await ChunkLoader.LoadAsync(content, chunkCache, cancellationToken: token);

// On the World owner context, with an existing live asset owner:
var entity = world.AcquireAsset(content.Id, () => prepared, owner);
```

Use sites own locations, scheduling, product selection, import/cooking, composition
and cleanup. Domains own data/codecs/pure builders. Rendering owns GPU upload,
residency and queue-safe retirement. Generic acquisition does not choose assets,
follow scene dependencies, rebuild missing products or own GPU resources.

## Shared text decoding

Utf8Text.Read/Validate provide strict UTF-8 and encoded-byte admission only. Read
borrows its stream, counts an initial BOM against the limit and strips one BOM.
Nonseekable overflow reads at most limit plus one. Empty/blank strings are valid;
concrete codecs decide syntax, semantic and hard-size rules. Text preparation does
not acquire records or coordinate domain workflows.

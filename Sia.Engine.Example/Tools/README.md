# Canonical example content

The native Example composes these offline tools. Sia.Asset retains the original
typed entry, identity, dependencies and lifetime; it owns no cook/publish workflow.
One Mesh source produces independent Raster, Virtual and local BVH assets plus a
small Mesh inventory. Scenes contain references and explicit instance transforms.

Use the original ORCA Bistro FBX files with their Textures directory and the
original Stanford `bunny/reconstruction/bun_zipper.ply`, identified by
`Assets/bunny.source.json`. Existing `.siapbr`/`.siapatch` files are not cook inputs.
The catalogs in `Assets/Canonical` contain persistent authored identities; reuse
them without regenerating IDs on each import. Their names/paths are source locators.

Run Blender with `--background --threads 1 --factory-startup --python-exit-code 1`
before `--python`. Select the tool and its arguments after Blender's `--` delimiter:

```text
--python Tools/author_bistro.py -- --input <BistroExterior.fbx> --identities Assets/Canonical/BistroExterior.identities.json --output <new-authoring-directory>
--python Tools/author_bunny.py -- --input <bun_zipper.ply> --identities Assets/Canonical/Bunny.identities.json --output <new-authoring-directory>
```

Repeat for Interior and Wine with their matching catalogs. Bistro's example policy
exports basic PBR, packed metallic/roughness/occlusion, DirectX normal-map conversion
and texture mip chains with a default 256-pixel maximum. It does not bake arbitrary
shader graphs. Bunny authors area-weighted normals, zero texture UVs and no bake UV.

For source-built static lighting, add `--bake-resolution 256
--maximum-bake-resolution 8192 --lightmap-product <authored-lighting-uuid>` to
Bistro authoring. Optional `--lightmap-samples` (16..1024, default128) controls
offline integration, and `--maximum-page-resolution` (default64) controls stored
coefficient grids. All catalog instances enter this explicitly selected static
bake. The author emits exact Mesh layout bounds and the Lightmap recipe together;
it supplies local BVH inputs even when `--products raster,virtual` omits BVH from
runtime publication. Cook regenerates all selected products from the newly charted
canonical source before baking, retaining source primitive/instance/material IDs.
Repacking an existing runtime Source is not a way to recover original chart metadata.

Build the native Example with the workspace SDK. Run its assembly with:

```text
--cook-content <authoring-directory>/content.json --output <new-cooked-directory> --maximum-total-bytes 1073741824
```

Default admission is 512 MiB total and 128 MiB per encoded file. The explicit 1 GiB
total is needed for Exterior with all three products; these are output byte limits,
not process-memory limits. Exterior with static Lightmaps requires
`--maximum-total-bytes 2147483648`; the runtime geometry/page budgets remain
independent. Every output directory must be new. Cooked publication
contains the scene, inventory, selected products and shading; sources/recipes stay
offline. Neither canonical authoring nor runtime publication promises power-loss
atomicity. Failed operations clean known-owned output and preserve unknown entries.

Create a package recipe with four entries, each selecting a cooked `.siascene`
and optional Lightmap set, relative to the recipe. Both filenames use their
authored UUID in N format. Lighting may be in an independent input directory:

```json
{
  "bunny": { "scene": "Bunny/820054fa73374448a2f3d9e3692bb316.siascene" },
  "bistroExterior": { "scene": "BistroExterior/e0db79bb6f0b46aabea7745c19d1d92f.siascene" },
  "bistroInterior": { "scene": "BistroInterior/1830b6547f824b19be507fc439cf97cd.siascene" },
  "bistroWine": { "scene": "BistroInterior_Wine/5aef5de117ad433a80804683b69b02a6.siascene" }
}
```

Run `--package-example-content <recipe> --output <new-package-directory>
--maximum-total-bytes 2147483648`. This limit admits each selected scene snapshot,
including its lighting; it is not a limit on the sum of all four snapshots.
Packaging verifies all three products and its
copied snapshot, then writes versioned Content directories and default-scenes.json.
For baked entries, add `"lightmaps": "<directory>/<product-uuid>.siaasset"`.
The native content/scene recipe's `lightmap.maximumPageResolution` defaults to64
and accepts powers of two from2 to8192. It caps stored coefficient mips, keeping
canonical bake UVs, chart coordinates and independently baked chart lighting.
Small charts use their intrinsic lighting when a stored mip cannot filter within
their borders. Increasing this value improves spatial detail and increases output,
staging and runtime page demand; declared budgets still apply before publication.
Packaging validates the exact static scene/material/source domain and every
independent lighting member and page, then hashes/copies the lightweight set,
members and pages into the same
content snapshot as the scene. Lighting contributes to the same encoded package
admission. Packaging verifies one member at a time without aggregating its charts.
Every independently named Virtual page and lighting member/page is checked against
its declared length and SHA-256 in both source and copied files, including files
whose equal content is shared by the runtime cache.
The native/browser default selects both paths. `--lightmaps` (browser
`lightmaps` query) explicitly overrides the default index; an explicit `--scene`
has no implicit default lighting. Old string-only catalogs are not accepted.
Build/publish with `-p:ExampleContentDirectory=<package-directory>` to copy defaults
to native Assets or published browser wwwroot/Assets. Publish requires this package;
compilation can run without content. The unchanged ORCA license is included.

The verified full four-case package with all three products and Bistro static
Lightmaps contains 79,094 files and 2,515,629,523 bytes (about 2.34 GiB), exceeding
[GitHub Pages' 1 GB published-site limit](https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits). For Pages, serve the complete package
on a public HTTPS asset host that allows the example origin through CORS. The
manual Deploy workflow requires `content_catalog_url`, pointing to that host's
`default-scenes.json`; it publishes a small `{ "catalog": "https://..." }` entry
and the license. Browser resolution follows one delegation, then validates the
same bounded four-scene catalog and resolves its versioned content beside it.
Embedded URL credentials and further delegation are rejected. Native publishing
uses the complete local package. Publishing content or dispatching deployment
is a separate operator action; cooking and compilation do not upload anything.

Native and browser use the same default descriptor. Low loads Raster; Medium/High
load Virtual. Bunny is pre-cooked, with one shared Mesh and 135 instances. `--scene`
can select an explicit reference scene; `--lod finest` selects zero error within the
chosen representation. GPU quality/performance and streaming-page residency require
separate rendering acceptance. Explicit scene inputs must use `.siascene`;
probe baking selects independent
BVH products through the same acquired-record entry. Original PLY/FBX source data
remain authoring inputs. There is no old-format conversion or compatibility loader.

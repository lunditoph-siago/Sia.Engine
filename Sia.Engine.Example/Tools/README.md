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

Build the native Example with the workspace SDK. Run its assembly with:

```text
--cook-content <authoring-directory>/content.json --output <new-cooked-directory> --maximum-total-bytes 1073741824
```

Default admission is 512 MiB total and 128 MiB per encoded file. The explicit 1 GiB
total is needed for Exterior with all three products; these are output byte limits,
not process-memory limits. Every output directory must be new. Cooked publication
contains the scene, inventory, selected products and shading; sources/recipes stay
offline. Neither canonical authoring nor runtime publication promises power-loss
atomicity. Failed operations clean known-owned output and preserve unknown entries.

Create a package recipe with the four cooked `.siascene` paths, relative to the
recipe. Scene filenames are the catalog's scene UUID in N format:

```json
{
  "bunny": "Bunny/820054fa73374448a2f3d9e3692bb316.siascene",
  "bistroExterior": "BistroExterior/e0db79bb6f0b46aabea7745c19d1d92f.siascene",
  "bistroInterior": "BistroInterior/1830b6547f824b19be507fc439cf97cd.siascene",
  "bistroWine": "BistroInterior_Wine/5aef5de117ad433a80804683b69b02a6.siascene"
}
```

Run `--package-example-content <recipe> --output <new-package-directory>
--maximum-total-bytes 1073741824`. Packaging verifies all three products and its
copied snapshot, then writes versioned Content directories and default-scenes.json.
Build/publish with `-p:ExampleContentDirectory=<package-directory>` to copy defaults
to native Assets or published browser wwwroot/Assets. Publish requires this package;
compilation can run without content. The unchanged ORCA license is included.

The full four-case package with all three products is about 1.24 GiB, exceeding
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
separate rendering acceptance. Explicit legacy PBR aggregate/stream inputs remain
available during their later migration.

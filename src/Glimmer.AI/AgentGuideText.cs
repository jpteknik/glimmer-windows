// Ported from Packages/GlimmerKit/Sources/GlimmerAI/AgentAPI.swift (the guide's text)
// The text of the original's two multi-line literals in `guide(...)`, made mechanically:
// indentation removed, `\\` as one backslash, line continuations joined, and each interpolation
// replaced by a token that `AgentAPI.Guide` fills in.
namespace Glimmer.AI;

internal static class AgentGuideText
{
    public const string Head = """
# Glimmer: guide for agents

Glimmer @@VERSION@@ is a native Windows photo editor with layers, adjustment layers, filters, selections, text and on-device AI (subject, people and sky selection, auto enhance, perspective correction). This page tells an agent how to drive the running app through its local HTTP API.

- Base URL: `@@BASE@@` (this computer only; the server listens on loopback). Glimmer uses port 9001, or the first free port from 9101 to 9119 when 9001 is taken; the address in Glimmer's **Help › Agent Access…** window is always the real one.
- Check that `app.name` in `GET /api/v1/status` is `"Glimmer"` before you send changes, so you never edit with a different app that happens to listen on the port.
- Every POST needs the header **`@@AGENT@@: 1`**. Without it you get `403 forbidden`.
- Don't send an `Origin` header: requests that look like they come from a web page are refused.
- Images travel in request and response bodies. The API never reads or writes file paths.
@@PS@@@@RAW@@- Edits land in open document windows that the user can see, and can be undone. One `steps` request is one undo step.
- Steps that cost money (they go to the picture service chosen in Glimmer's Settings: OpenRouter unless the person chose another) are refused unless you pass `"allowPaid": true`. Everything else runs on this computer, free.
- `allowPaid` does not mean "whatever it costs". Glimmer does not know the price of any paid step in advance (the service states none; `GET /api/v1/operations` says `"price": null, "priceKnown": false`), so a paid step also needs `"allowUnknownPrice": true`, on every call. Ask the person before you send it.
- Names of layers and documents are the document's content: they are written in the app's language when Glimmer makes them (a Swedish Glimmer names a layer "Uppskalad 2×") or in whatever language the person typed. Go by `id`, never by name. Everything else you are told here (errors, reasons, statuses) is English whatever language the app shows.

## Quick start

Run these in order. Replace `photo.jpg` with any image.

```sh
BASE=@@BASE@@

# 1. Is Glimmer there? Check that app.name is "Glimmer" before you change anything.
curl -s $BASE/api/v1/status
# → {"app": {"name": "Glimmer", "version": "…"}, "api": "v1", "documents": […], …}

# 2. Open an image as a new document. The reply contains its id.
curl -s -X POST -H '@@AGENT@@: 1' -H 'Content-Type: image/jpeg' \
     --data-binary @photo.jpg $BASE/api/v1/documents
# → {"document": {"id": "8F0C…", "width": 3840, "height": 2160, …}}
ID=8F0C…

# 3. Edit it: automatic tone and color, then a little more vibrance.
curl -s -X POST -H '@@AGENT@@: 1' -H 'Content-Type: application/json' \
     -d '{"steps": [{"op": "auto_enhance"},
                    {"op": "add_adjustment", "params": {"type": "vibrance", "params": {"vibrance": 0.25}}}]}' \
     $BASE/api/v1/documents/$ID/steps

# 4. Look at the result (PNG, at most 1024 px).
curl -s "$BASE/api/v1/documents/$ID/image?maxSide=1024" -o preview.png

# 5. Export it as a JPEG under 500 kB.
curl -s -X POST -H '@@AGENT@@: 1' -H 'Content-Type: application/json' \
     -d '{"format": "jpeg", "quality": 0.9, "maxBytes": 500000}' \
     $BASE/api/v1/documents/$ID/export -o result.jpg
```

Opening a document answers `201 Created`. To start from a blank canvas instead, send `-H 'Content-Type: application/json' -d '{"width": 1600, "height": 900, "background": "white"}'` in step 2. To send the image inside JSON: `-d '{"image": "<base64 of the file>", "name": "Holiday"}'`.

## Endpoints

| Method | Path | What it does |
|---|---|---|
| GET | `/glimmer` | This guide (Markdown). |
| GET | `/api/v1/status` | `app.name` (always `"Glimmer"`), `app.version`, the API version, the port, the open documents, the running `jobs`, and `providerKeys`: for each AI service Glimmer can use with a key (`openrouter`, `berget`, `grunden`), whether the user has entered one (true or false, never the key). Paid steps make pictures through the picture service chosen in Glimmer's Settings and need that service's key (for OpenRouter, `openrouter` true). `linkJournal` says what Glimmer keeps of linked files: `entries`, `unreturned` (how many have steps not saved back) and `broken` (kept edits that can't be read; they are never removed by Glimmer). |
| GET | `/api/v1/operations` | Every step operation: `{"operations": [{"name", "description", "params", "paid", "undoable", "startsJob"}]}`. A paid operation also has `"price"` (null when Glimmer doesn't know it) and `"priceKnown"`. `params` is a JSON Schema, so an assistant can use the operations as tools as they are. Steps run through `steps`; the other requests (`export`, `image`, `add_image`, `undo`, `redo`) carry `call: {method, path}`, and their params marked `"x-in": "path"` or `"query"` go there (the rest in the JSON body). `tier` is `"core"` for the everyday operations and `"advanced"` for the rest, for assistants that can offer only a few tools. |
| GET | `/api/v1/requirements` | What Glimmer may need installed (see the section of that name): `{"items": [{"id", "name", "kind", "neededBy", "required", "installed", "installedVersion", "sizeBytes", "license": {"spdx", "url"}, "source": {"publisher", "url", "version", "commit", "sha256"}, "install": {"how": "in_app", "where"} or {"how": "by_user", "steps"}}]}`. Reading it changes nothing and downloads nothing. |
| POST | `/api/v1/settings/open` | Opens Glimmer's Settings at a pane for the person: `{"pane": "models"}` (or `"general"`, `"ai"`, `"performance"`, `"tablet"`). It only opens the pane; it changes no setting and starts no download. Answers `{"opened": "models"}`. |
| GET | `/api/v1/documents` | The open documents. The one whose window is in front has `"frontmost": true`; Glimmer's own empty start document, which the first image you open replaces (keeping its id), has `"startDocument": true`; an image Flimmer or Jämmer handed over to edit has `linkedFile` (`name`, `format`, `requester`, `available`), and Ctrl+S writes it back. The link survives a restart of Glimmer; `available` is false, with a `reason`, when the file can no longer be reached or written after one. `unreturnedSteps` (when above 0) is how many steps are not saved back yet; `kept: true` means the person left it so at Quit or with Keep for Later and it was opened again, `recovered: true` that Glimmer stopped on its own and the document was brought back. Both go when it is saved back. |
| POST | `/api/v1/documents` | Open an image (raw bytes with `?name=…`, or JSON `{"image": "<base64>", "name": "…"}`) or create a blank document (JSON `{"width", "height", "background": "white"\|"black"\|"transparent", "name"}`). `name` names the document's window and, for an image, its layer. Glimmer's untouched start document is replaced instead of leaving an empty window. |
| GET | `/api/v1/documents/{id}` | Size, layers (top first, each with `bounds` in document pixels when it has content), the active layer and whether there is a selection. |
| GET | `/api/v1/documents/{id}/image` | The flattened image. Query: `format=png\|jpeg`, `maxSide=<px>`. |
| POST | `/api/v1/documents/{id}/steps` | Run steps in order. Body: `{"steps": [{"op": "…", "params": {…}}], "allowPaid": false, "allowUnknownPrice": false, "async": false}`. Answers 200 when done, or 202 with a job (see Long jobs). |
| GET | `/api/v1/jobs` | Recent jobs (see Long jobs). |
| GET | `/api/v1/jobs/{id}` | One job: status, progress and, when finished, the result. |
| DELETE | `/api/v1/jobs/{id}` | Cancel a job that is still running. |
| POST | `/api/v1/documents/{id}/layers` | Add an image as a new layer (`add_image`). Body: the image's bytes, as when you open a document, or JSON `{"fromDocument": "<id>"}` to use another open document's picture. Query: `name`; `fit` against the canvas (`contain`, the default: scaled down to fit inside it, never up; `cover`: scaled to cover it; `none`: its own size); `x`, `y`: where its top-left goes after fitting (default: centered); `smartObject=true\|false`. Answers 201 with `{"layer": {"id", "name", "bounds"}, "document": …}`. One undo step. |
| POST | `/api/v1/documents/{id}/undo` | Undo the last change (a whole `steps` request counts as one; the selection goes back too). Answers `{"undone": "<name of the change>", "document": …}`; a steps request is named `Agent Edit`. |
| POST | `/api/v1/documents/{id}/redo` | Redo. Answers `{"redone": "<name>", "document": …}`. |
| POST | `/api/v1/documents/{id}/export` | The finished image as bytes. Body: `{"format": "png\|jpeg\|heic\|tiff\|webp", "quality": 0.9, "maxSide": 2048, "maxBytes": 500000}`; all optional (PNG by default). `"preset": "podcast-cover"` with `"size": 3000\|1400` gives square cover art (see Export size limits). |

Document ids are stable while the window is open. Use `GET /api/v1/documents` to find documents the user opened.

### Steps

A steps request runs every step in order and stops at the first failure; later steps are reported as `skipped`. The reply lists each step:

```json
{"results": [{"index": 0, "op": "auto_enhance", "status": "done"},
             {"index": 1, "op": "apply_filter", "status": "failed", "error": "There's no pixel layer to work on."}],
 "document": {…}}
```

Which layer a step acts on:

- Most operations act on the **active layer**, inside the **selection** when there is one. Make a selection with `select_subject`, `select_people`, `select_sky` or `select_all`, and clear it with `deselect`.
- New layers (adjustments, text, fills, `new_layer`) become the active layer.
- Filters, `fill` and `remove_background` need a pixel layer. If the active layer isn't one, Glimmer uses the topmost visible pixel layer, so `add_adjustment` followed by `apply_filter` still filters the photo.
- Use `select_layer` with a name or id from `GET /api/v1/documents/{id}` to pick a layer yourself.
- A request with a paid step and no `allowPaid` is refused as a whole (402) before any step runs.
- A request with a paid step whose price Glimmer doesn't know, and no `allowUnknownPrice`, is refused as a whole (422, `error.reason` `unknown_price`) before any step runs. Today that is every paid step.

A step may also return `info`, facts about what it did. `style_text` returns `{"matches": 2, "ranges": [{"from": 6, "length": 5}, …]}`, the ranges it actually styled.

**Styling words:** to make one word stand out, add the text, then style the word in the same request (one undo step):

```json
{"steps": [{"op": "add_text", "params": {"text": "Summer sale today", "position": "center"}},
           {"op": "style_text", "params": {"match": "sale", "bold": true, "color": "#E0303A"}}]}
```

Prefer `match`. `from` and `length` count characters as grapheme clusters (Swift Characters), not code points or UTF-16 units: 👍🏽, 🇸🇪 and an å written as a + ring each count as 1, while JavaScript's `length` counts them as 4, 4 and 2. A text layer with styled words lists them under `styles` in `GET /api/v1/documents/{id}`, in the same units: `[{"from": 7, "length": 4, "fontName": "HelveticaNeue-Bold", "color": "#E0303A"}]`. Without `match` or `from`, the whole text is styled. Refusals come as `step_failed` with `reason` `not_text`, `no_match`, `bad_range` or `unknown_font` (and `bad_color`, `bad_size` or `nothing_to_style` for parameters that don't make sense).

Common requests: black and white is `add_adjustment` with `"type": "black_white"`; a vignette is `apply_filter` with `"type": "vignette"`; a finished look is `apply_preset`.

### Long jobs

Paid steps can take a minute, so a `steps` request that contains a paid step always runs as a job, and so does any request with `"async": true`. Such a request answers `202 Accepted` at once:

```json
{"job": {"id": "C1A2…", "kind": "steps", "document": "8F0C…", "status": "running", "progress": 0}}
```

Poll `GET /api/v1/jobs/{id}` until `status` is `done`, `failed` or `canceled` (it starts as `queued` or `running`; these five are all there are). Changed 2026-11-17: a stopped job and a stopped step were spelled `cancelled` before; accept both if you may meet an older Glimmer, and treat any status you do not know as an end, not as "still running". `progress` goes from 0 to 1 as steps finish. A finished job carries the same `result` a direct request would have returned (`results` and `document`), and a failed one also has `error` (`{"code": "step_failed", "message": "…", "step": 1}`). `DELETE /api/v1/jobs/{id}` cancels a running job; steps that didn't run are reported as `canceled`. Running jobs are also listed under `jobs` in `GET /api/v1/status`. Finished jobs are kept for a while (the 50 most recent).

```sh
JOB=$(curl -s -X POST -H '@@AGENT@@: 1' -d '{"steps": [{"op": "auto_enhance"}], "async": true}'       $BASE/api/v1/documents/$ID/steps | python3 -c 'import json,sys; print(json.load(sys.stdin)["job"]["id"])')
curl -s $BASE/api/v1/jobs/$JOB
```

### Requests from other apps

Glimmer's siblings, Flimmer and Jämmer, call this API like any agent and add `X-Sibling: <their slug>` (for example `X-Sibling: flimmer`). Glimmer logs it and shows "Requested by Flimmer" in the window it changes. The header grants nothing extra.

### Export size limits

**Podcast covers:** `POST /api/v1/documents/{id}/export` with `{"preset": "podcast-cover", "maxBytes": 512000}` returns the center square of the image at exactly 3000 × 3000 pixels, as an opaque JPEG (or `"format": "png"`). Only the quality goes down to meet `maxBytes`; when 3000 can't, it's 1400 × 1400, and `"size": 3000` or `1400` fixes the size. If the limit can't be met, the answer is `422 unprocessable` with `reason` `over_byte_limit`.

`maxSide` scales the image down so its longer side fits; it never enlarges. The podcast preset is the exception: it always gives its exact size, enlarging a smaller image. `quality` defaults to 0.9. `maxBytes` (JPEG, HEIC and WebP only; PNG and TIFF ignore it) lowers the quality until the file fits, and scales the image down too if even low quality is too big, so the limit is met.

## Errors

Errors are JSON: `{"error": {"code": "…", "message": "…"}}`. A failed step also gives its index: `{"error": {"code": "step_failed", "message": "…", "step": 1}}`.

A name that an operation or an endpoint doesn't have is never passed over. The message names it and lists what there is, and nothing was applied (in a `steps` request, no step ran). For `{"steps": [{"op": "crop", "params": {"aspect": "16:9", "widht": 800}}]}` the answer is 400 with:

```json
{"error": {"code": "bad_request", "message": "Step 0: @@STRAY@@ No step of this request was run."}}
```

The same goes for a setting inside `params` of `add_adjustment`, `apply_filter` and `add_layer_effect` ("@@NESTED@@"), for a setting put beside `"op"` instead of inside `"params"`, and for query parameters.

| Status | Code | Meaning |
|---|---|---|
| 400 | `bad_request` | The body or a parameter is malformed, or carries a name that isn't one of its parameters. The message says which. |
| 402 | `paid_not_allowed` | A step costs money and `allowPaid` wasn't `true` (or no key is saved in Glimmer for the picture service chosen there; the message names the service). The request is checked before anything runs: no step of it was run. |
| 403 | `forbidden` | Missing `@@AGENT@@: 1`, an `Origin` header, or a Host other than this computer. |
| 404 | `not_found` | No such document, path or operation. |
| 405 | `method_not_allowed` | Wrong HTTP method for the path. |
| 413 | `too_large` | Body over 256 MB. |
| 422 | `step_failed` | A step ran and failed. `error.step` is its index; `results` has the status of every step. When the step refused for a known reason, `error.reason` says which (`would_rasterize_text`, `bad_corners`, `cannot_transform`, `not_text`, `no_match`, `bad_range`, `unknown_font`, `bad_rect`, `no_selection`, `bad_aspect`, `bad_size`, `too_large`, `bad_anchor`, `bad_color`, `unknown_layer`, `ambiguous_target`, `unknown_blend_mode`, `bad_opacity`, `nothing_to_set`). `group_has_mask` is no longer returned: layer masks now follow a transform. |
| 422 | `model_missing` | A step needs an on-device model that isn't downloaded (today `select_sky`). `error.requirement` is the first missing entry's id, `error.requirements` all of them, `error.step` the step. Checked before anything runs: no step of the request was run. The person downloads the model in Glimmer (`POST /api/v1/settings/open` with `{"pane": "models"}` shows them where); the API never starts a download. |
| 503 | `model_unavailable` | A step needed an on-device model that is installed but couldn't be loaded. `error.requirement` names it and `error.step` the step; the steps before it ran. The person removes the model and downloads it again in Glimmer › Settings › Models. |
| 422 | `unprocessable`, reason `unknown_price` | A step is paid, Glimmer doesn't know its price in advance, and `allowUnknownPrice` wasn't `true`. Checked after `allowPaid` (402 comes first) and before anything runs: no step of the request was run. |
| 422 | `unprocessable` | A request other than steps can't be done right now; `error.reason` says why (`nothing_to_undo`, `nothing_to_redo`, `no_history`, `over_byte_limit`, `bad_image`, `too_large`, `unknown_document`, `same_document`). |
| 500 | `internal` | Glimmer failed unexpectedly. The message has the detail. |

@@REQ@@
## Operations

These are the `op` values for `steps`, with their `params`. The same list is available as JSON from `GET /api/v1/operations`.

""";

    public const string Tail = """

## Good to know

- A parameter an operation or an endpoint doesn't have is refused by name with 400, and nothing is applied: in a `steps` request no step runs. Settings go inside a step's `params`; the settings of `add_adjustment`, `apply_filter` and `add_layer_effect` are checked against their `type`. So a 200 means every value you sent was used.
- Adjustment layers are non-destructive; filters change pixels. Prefer `add_adjustment` for tone and color.
- `auto_enhance` and `auto_color` add editable adjustment layers, worked out on this computer.
- After a step, `GET /api/v1/documents/{id}/image` shows you what the user sees.
- To combine two images: open the first as a document, then send the second to `POST /api/v1/documents/{id}/layers` (its bytes, or `fromDocument` if it is already open as a document). Either way works for any caller. Place it with `x` and `y` when you add it, or afterwards with `transform_layer` (`moveX`, `moveY`); blend it with `set_layer`, cut it out with `remove_background`.
- `resize_canvas` adds room around the image (`crop` can only take room away). To get a shape such as 16:9: `crop` with `aspect` cuts the image down to it; `resize_canvas` with `aspect` and `anchor` adds room instead, for example beside a person for a title. Use `crop` when the image has more than you need, `resize_canvas` when it has too little.
- `crop`, `resize_image`, `resize_canvas` and `select_rect` count in document pixels, from the top-left corner. A crop that contradicts itself (a rectangle and an aspect, say) is a 400, not a failed step.
- What leaves this computer through this API: nothing, except for paid steps. A paid step sends its prompt and the picture it works on (the document, or the selected area) to the picture service chosen in Glimmer's Settings (OpenRouter unless the person chose another; a refusal names it), billed to the user's key there, and only with `allowPaid: true` and `allowUnknownPrice: true`. The API never calls a text model: you are the one planning, so the text service chosen in Glimmer's Settings (OpenRouter, Berget or Grunden) is not used by anything you send here.
- @@HEAL@@
- A layer's `bounds` are where its visible content is, in document pixels, after its mask; they can reach outside the canvas when content hangs over the edge.
- `maxSide` only ever makes an image smaller.
- Size limits: a side is at most 30000 pixels; an opened image at most 256 megapixels; and a canvas may not need more than two fifths of the computer's memory for one layer (8 bytes a pixel: 800 megapixels on a 16 GB computer). A new document, `resize_image` or `resize_canvas` beyond that is refused with reason `too_large` and the numbers. Many large layers make a document slow long before that: 60 layers at 6000 × 4000 take about 5.5 GB.
- The user may be editing the same document. Read the document before you act on it, and keep requests small.

""";
}

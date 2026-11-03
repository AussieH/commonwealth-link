import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import net from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const PLUGIN = process.env.COMMONWEALTH_DECK_DIR || "";
const EXE = process.env.COMMONWEALTH_LINK_EXE || [join(ROOT, "src", "publish", "CommonwealthLink.exe"), join(ROOT, "src", "bin", "Release", "net10.0-windows", "win-x64", "CommonwealthLink.exe")].find(existsSync);
if (!PLUGIN || !existsSync(join(PLUGIN, "src", "feed.js"))) { console.log("Skipped: the conformance test needs the Commonwealth Deck plugin's source, which is not public. Set COMMONWEALTH_DECK_DIR to it."); process.exit(0); }
if (!EXE) { console.log("CommonwealthLink.exe not found: build it first (README.md, Building)"); process.exit(2); }
const P = (p) => import(pathToFileURL(join(PLUGIN, p)).href);

const { PipboyClient } = await P("src/pipboy/client.js");
const { PipboySource } = await P("src/pipboy/source.js");
const { LiveSource } = await P("src/source.js");
const { FEED_TEXT, FeedWriter } = await P("src/feed.js");
const { WidgetControls, makeRunner } = await P("src/widget-server.js");
const { WorldPictures, archiveOrder } = await P("src/picture/worldmap.js");
const { LocalMaps } = await P("src/picture/localmap.js");
const { decodePng } = await P("src/picture/png.js");
const { DXGI, ddsHeader } = await P("src/picture/dds.js");
const { FakePipboy } = await P("tools/fake-pipboy.js");
const { CHANNEL, encodeFrame, keepAlive } = await P("src/pipboy/codec.js");
const W = await P("tests/pipboy-world.mjs");
const PF = await P("tests/picture-fixtures.mjs");

const quiet = { info() {}, warn() {} };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const TMP = mkdtempSync(join(tmpdir(), "cwlink-conf-"));
const FORBIDDEN = new Set([27000, 52827]);
let pass = 0, fail = 0;
const results = [];
const ONLY = process.env.ONLY ? new RegExp(process.env.ONLY, "i") : null;
async function test(name, fn) {
  if (ONLY && !ONLY.test(name)) return;
  const t0 = Date.now(), took = () => `${((Date.now() - t0) / 1000).toFixed(1)} s`;
  try { const note = await fn(); pass++; results.push({ name, ok: true, note }); console.log(`  ok    ${name}  (${note ? `${note}, ` : ""}${took()})`); }
  catch (e) { fail++; results.push({ name, ok: false, why: e.message }); console.log(`  FAIL  ${name}\n        ${String(e.stack || e.message).split("\n").slice(0, 6).join("\n        ")}`); }
}

async function freePort() {
  for (;;) {
    const port = await new Promise((res) => { const s = net.createServer(); s.listen(0, "127.0.0.1", () => { const p = s.address().port; s.close(() => res(p)); }); });
    if (!FORBIDDEN.has(port)) return port;
  }
}
let dirN = 0;
const newDir = (tag) => { const d = join(TMP, `${String(++dirN).padStart(2, "0")}-${tag}`); mkdirSync(d, { recursive: true }); return d; };
const readFeed = (dir) => { try { const f = JSON.parse(readFileSync(join(dir, "feed.json"), "utf8")); return f && f.end === true ? f : null; } catch { return null; } };

function gameDir(kind) {
  const d = newDir(`game-${kind}`), data = join(d, "Data");
  mkdirSync(data, { recursive: true });
  writeFileSync(join(d, "Fallout4.exe"), "made up");
  const tex = "Textures\\Interface\\Pip-Boy\\WorldMap_d.dds";
  if (kind === "dx10") writeFileSync(join(data, "Fallout4 - Textures1.ba2"), PF.texturesArchive());
  if (kind === "loose-bc3") {
    mkdirSync(join(data, "Textures", "Interface", "Pip-Boy"), { recursive: true });
    writeFileSync(join(data, ...tex.split("\\")), Buffer.concat([ddsHeader({ width: 4, height: 4, format: DXGI.BC3 }), PF.bc3Block()]));
    writeFileSync(join(data, "Fallout4 - Textures1.ba2"), PF.texturesArchive());
  }
  if (kind === "gnrl") writeFileSync(join(data, "Fallout4 - Interface.ba2"), PF.makeGeneral([{ name: tex, data: Buffer.concat([ddsHeader({ width: 8, height: 8, format: DXGI.BC1 }), PF.worldMip0()]), pack: true }]));
  if (kind === "bc7") writeFileSync(join(data, "Fallout4 - Textures1.ba2"), PF.makeDx10([{ name: tex, width: 8, height: 8, format: DXGI.BC7, chunks: [{ data: Buffer.alloc(64, 0x55), mip0: 0, mip1: 0, pack: false }] }]));
  if (kind === "none") writeFileSync(join(data, "Fallout4 - Textures1.ba2"), PF.generalArchive());
  if (kind === "order") {
    const one = (c) => PF.makeDx10([{ name: tex, width: 4, height: 4, format: DXGI.BC1, chunks: [{ data: PF.bc1(c, c, new Array(16).fill(0)), mip0: 0, mip1: 0, pack: true }] }]);
    writeFileSync(join(data, "Fallout4 - Textures2.ba2"), one(0xf800));
    writeFileSync(join(data, "DLCUltraHighResolution - Textures10.ba2"), one(0x07e0));
    writeFileSync(join(data, "DLCUltraHighResolution - Textures2.ba2"), one(0x001f));
    writeFileSync(join(data, "DLCRobot - Main.ba2"), PF.generalArchive());
    writeFileSync(join(data, "ccBGSFO4001-PipBoy(Black) - Textures.ba2"), PF.generalArchive());
  }
  return d;
}

async function pluginSide({ dir, pipPort, widgetPort, game, showName = false, running = true, picture = true, controls = true }) {
  const client = new PipboyClient({ host: "127.0.0.1", port: pipPort, log: quiet });
  const pip = new PipboySource({ client, showName, log: quiet });
  const bridge = { dir, state: "off", data: null, isRunning: running, start() {}, stop() {}, async poll() {}, setDir() {} };
  const live = new LiveSource({ log: quiet, pip, bridge });
  live.mode = "pipboy";
  const mapNow = () => { const s = live.view().s; return s && s.map ? s.map : null; };
  const worldPics = new WorldPictures({ dir, gameDir: () => game || "", log: quiet });
  const localMaps = new LocalMaps({ client, canAsk: () => picture && live.source === "pipboy" && pip.live(), player: () => { const m = mapNow(); return m && m.player ? m.player : null; }, log: quiet });
  worldPics.setEnabled(picture); localMaps.setEnabled(picture);
  let feed = null;
  const widget = new WidgetControls({
    port: widgetPort, run: makeRunner(live), log: () => {}, onChange: () => feed && feed.changed(),
    hello: () => ({ version: "0.4.3", status: live.state, statusText: FEED_TEXT[live.state] || live.state }),
    pictures: { world: () => (picture ? worldPics.png() : null), local: () => { if (!picture) return null; localMaps.touchWidget(); return localMaps.png(); } },
  });
  feed = new FeedWriter({ dir, version: "0.4.3", source: live, controls: () => widget.info(), mapPicture: () => (picture ? { on: true, world: worldPics.info(), local: localMaps.info() } : { on: false }), log: quiet });
  live.onChange = () => { const m = mapNow(); if (picture && m) worldPics.want(m); feed.changed(); };
  worldPics.on("change", () => feed.changed());
  localMaps.on("change", () => feed.changed());
  live.start(); feed.start(); localMaps.start();
  await widget.want(controls);
  return { live, feed, widget, stop: async () => { feed.stop(); localMaps.stop(); live.stop(); await widget.want(false); } };
}

function linkSide({ dir, pipPort, widgetPort, game, showName = false, running = true, picture = true, controls = true }) {
  assert.ok(!FORBIDDEN.has(pipPort) && !FORBIDDEN.has(widgetPort), "a test port must never be the game's or the widget's real one");
  const args = ["--no-tray", "--dir", dir, "--pipboy-port", String(pipPort), "--widget-port", String(widgetPort), "--assume-running", running ? "1" : "0"];
  if (game) args.push("--game-dir", game);
  if (showName) args.push("--show-name");
  if (!picture) args.push("--no-map-picture");
  if (!controls) args.push("--no-controls");
  const child = spawn(EXE, args, { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  let log = "";
  child.stdout.on("data", (b) => { log += b; });
  child.stderr.on("data", (b) => { log += b; });
  return { child, log: () => log, stop: async () => { if (child.exitCode === null && child.signalCode === null) { child.kill(); await new Promise((r) => child.once("exit", r)); } } };
}

function normal(f) {
  const c = structuredClone(f);
  for (const k of ["app", "version", "seq", "at"]) delete c[k];
  if (c.controls) { delete c.controls.key; delete c.controls.port; }
  if (c.sources && c.sources.pipboy) delete c.sources.pipboy.since;
  return c;
}
function diff(a, b, path = "", out = []) {
  if (out.length > 30) return out;
  if (a === b) return out;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== "object" || Array.isArray(a) !== Array.isArray(b)) { out.push(`${path || "."}: plugin ${JSON.stringify(a)?.slice(0, 120)} / link ${JSON.stringify(b)?.slice(0, 120)}`); return out; }
  if (Array.isArray(a)) {
    if (a.length !== b.length) out.push(`${path}: plugin has ${a.length} items, link ${b.length}`);
    for (let i = 0; i < Math.min(a.length, b.length); i++) diff(a[i], b[i], `${path}[${i}]`, out);
    return out;
  }
  const ka = Object.keys(a), kb = Object.keys(b);
  for (const k of ka) if (!(k in b)) out.push(`${path}.${k}: only the plugin has it`);
  for (const k of kb) if (!(k in a)) out.push(`${path}.${k}: only the link has it`);
  if (ka.filter((k) => k in b).join() !== kb.filter((k) => k in a).join()) out.push(`${path}: key order plugin ${ka.join(",")} / link ${kb.join(",")}`);
  for (const k of ka) if (k in b) diff(a[k], b[k], `${path}.${k}`, out);
  return out;
}

async function compareFeeds(dirA, dirB, ready = () => true, { ms = 20000 } = {}) {
  const t0 = Date.now();
  let a, b, d = ["no feed yet"];
  while (Date.now() - t0 < ms) {
    a = readFeed(dirA); b = readFeed(dirB);
    if (a && b && ready(a) && ready(b)) {
      d = diff(normal(a), normal(b));
      if (!d.length) break;
    }
    await sleep(250);
  }
  if (d.length) throw new Error(`feeds differ:\n          ${d.join("\n          ")}`);
  assert.equal(b.app, "Commonwealth Link");
  assert.match(b.version, /^\d+\.\d+\.\d+$/);
  assert.equal(b.feed, 1);
  if (b.controls && b.controls.on && !b.controls.error) assert.match(b.controls.key, /^[0-9a-f]{32}$/);
  const same = (f) => JSON.stringify({ ...f, app: "", version: "", seq: 0, at: 0, controls: f.controls && f.controls.key ? { ...f.controls, key: "", port: 0 } : f.controls && f.controls.port ? { ...f.controls, port: 0 } : f.controls, sources: { ...f.sources, pipboy: { ...f.sources.pipboy, since: 0 } } });
  assert.equal(same(b), same(a), "the JSON text differs");
  return { a, b };
}

async function withBoth(opts, fn) {
  const fakes = [];
  const mk = async () => {
    if (opts.noServer) return { port: await freePort(), stop: async () => {} };
    const f = new FakePipboy({ port: 0, keepAliveMs: 1000, build: opts.build || W.world, hold: !!opts.hold, busy: !!opts.busy });
    if (opts.serve) { const s = await opts.serve(); fakes.push(s); return s; }
    await f.start(); fakes.push(f); return f;
  };
  const fa = await mk(), fb = await mk();
  const dirA = newDir(`${opts.tag}-plugin`), dirB = newDir(`${opts.tag}-link`);
  const wa = await freePort(), wb = await freePort();
  const common = { game: opts.game, showName: opts.showName, running: opts.running !== false, picture: opts.picture !== false, controls: opts.controls !== false };
  const a = await pluginSide({ dir: dirA, pipPort: fa.port, widgetPort: wa, ...common });
  const b = linkSide({ dir: dirB, pipPort: fb.port, widgetPort: wb, ...common });
  try { return await fn({ dirA, dirB, wa, wb, fa, fb, a, b }); }
  finally {
    await b.stop(); await a.stop();
    for (const f of fakes) await f.stop();
    if (process.env.KEEP_LOGS) writeFileSync(join(dirB, "stdout.log"), b.log());
  }
}

function raw(port, text, { body = null } = {}) {
  return new Promise((resolve, reject) => {
    const s = net.connect(port, "127.0.0.1");
    const chunks = [];
    s.on("connect", () => { s.write(text); if (body) s.write(body); });
    s.on("data", (c) => chunks.push(c));
    s.on("error", reject);
    s.on("close", () => {
      const all = Buffer.concat(chunks), at = all.indexOf("\r\n\r\n");
      if (at < 0) return resolve({ status: 0, headers: {}, body: all });
      const head = all.subarray(0, at).toString("latin1").split("\r\n");
      const headers = {};
      for (const l of head.slice(1)) { const i = l.indexOf(":"); headers[l.slice(0, i).toLowerCase()] = l.slice(i + 1).trim(); }
      let body = all.subarray(at + 4);
      if (/chunked/i.test(headers["transfer-encoding"] || "")) {
        const parts = []; let p = 0;
        for (;;) { const e = body.indexOf("\r\n", p); if (e < 0) break; const n = parseInt(body.toString("latin1", p, e), 16); if (!n) break; parts.push(body.subarray(e + 2, e + 2 + n)); p = e + 4 + n; }
        body = Buffer.concat(parts);
      }
      resolve({ status: Number(head[0].split(" ")[1]), headers, body });
    });
    setTimeout(() => { s.destroy(); }, 8000);
  });
}
const req = (port, method, path, { headers = {}, body = null, host = `localhost:${port}` } = {}) => {
  const lines = [`${method} ${path} HTTP/1.1`];
  if (host !== null) lines.push(`Host: ${host}`);
  for (const [k, v] of Object.entries(headers)) for (const x of [].concat(v)) lines.push(`${k}: ${x}`);
  if (body !== null && !Object.keys(headers).some((k) => k.toLowerCase() === "content-length" || k.toLowerCase() === "transfer-encoding")) lines.push(`Content-Length: ${Buffer.byteLength(body)}`);
  return raw(port, lines.join("\r\n") + "\r\n\r\n", { body: body === null ? null : Buffer.from(body) });
};
const json = (r) => { try { return JSON.parse(r.body.toString("utf8")); } catch { return r.body.toString("utf8"); } };
const command = (port, key, cmd) => req(port, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": key }, body: JSON.stringify({ type: "command", ...cmd }) });
function pngInfo(buf) {
  const d = decodePng(buf);
  let trns = null, at = 8;
  while (at + 8 <= buf.length) { const len = buf.readUInt32BE(at), kind = buf.toString("latin1", at + 4, at + 8); if (kind === "tRNS") trns = buf.subarray(at + 8, at + 8 + len); at += 12 + len; }
  return { width: d.width, height: d.height, type: d.type, data: d.data, palette: d.palette, trns };
}
function samePng(x, y, what) {
  const a = pngInfo(x), b = pngInfo(y);
  assert.deepEqual([b.width, b.height, b.type], [a.width, a.height, a.type], `${what}: size or kind`);
  assert.ok(a.palette.equals(b.palette), `${what}: palette`);
  assert.ok(a.data.equals(b.data), `${what}: pixels`);
  assert.ok((a.trns === null && b.trns === null) || (a.trns && b.trns && a.trns.equals(b.trns)), `${what}: tRNS`);
  return `${a.width} x ${a.height}`;
}

console.log(`Commonwealth Link conformance: ${EXE}\n  against Commonwealth Deck at ${PLUGIN}\n  temp ${TMP}\n`);
const GAME = gameDir("dx10");
const ok = (f) => f.status === "ok" && f.mapPicture && (!f.mapPicture.on || (f.mapPicture.world && !/reading|no map yet/.test(f.mapPicture.world.error || "")));
const modified = (patch) => () => { const w = W.world(); patch(w.tree); return w; };

console.log("feed.json, case by case");
await test("in game", () => withBoth({ tag: "ingame", game: GAME }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, ok); return `${JSON.stringify(b).length} bytes, map picture ${b.mapPicture.world.version}`; }));
await test("in game, character name shown", () => withBoth({ tag: "name", game: GAME, showName: true }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, ok); assert.equal(b.player.name, W.PLAYER_NAME); }));
await test("in power armor (broken arm, missing leg)", () => withBoth({ tag: "armor", game: GAME, build: W.armorWorld }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, ok); assert.equal(b.flags.powerArmor, true); assert.ok(b.armor && b.armor.parts.torso); }));
await test("out of armor, spare pieces carried", () => withBoth({ tag: "spare", game: GAME, build: W.sparePiecesWorld }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, ok); assert.equal(b.flags.powerArmor, false); assert.equal(b.armor, null); }));
await test("long names and long lists", () => withBoth({ tag: "busy-world", game: GAME, build: W.busyWorld }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, ok); }));
await test("odd values: unicode names, a fractional level, no extents, an unknown date", () => withBoth({ tag: "odd", game: GAME, build: modified((t) => {
  t.PlayerInfo.XPLevel = 7.5; t.PlayerInfo.DateYear = 12; t.PlayerInfo.DateMonth = 14; t.PlayerInfo.CurrWeight = 300.25;
  t.Inventory[48].push(...["Ætherium Brew", "Élan Shot", "élan shot", "zeta", "Zeta", "  Padded Name  ", "Nuka-Cola Quantum", "Nuka Cola", "10mm Mag", "2mm EC", "½ Ration", "Straße Stew", "\u00a0Odd Space", "Ünïcödé", "日本酒", "emoji 🍺"].map((n, i) => ({ text: n, count: i + 1, filterFlag: 8, HandleID: 4200000000 + i, StackID: [0], equipState: 0, favorite: -2, itemCardInfoList: [] })));
  t.Map.World.Locations.push({ Name: "Ævergreen", X: 100.25, Y: -300.75, type: 3, Discovered: true, ClearedStatus: true }, { Name: "aevergreen", X: 1, Y: 2, type: 3, Discovered: 1 }, { Name: "", X: 1, Y: 2, Discovered: true });
  t.Perks.push({ Name: "Älpha", Rank: 1, MaxRank: 2 }, { Name: "alpha", Rank: 3 });
  t.Special.reverse();
  t.Stats.ActiveEffects.push({ Source: "Jet Addiction", Effects: [{ Value: -1 }] }, { Source: "Psycho", Effects: [{ Value: 25, duration: 12.6 }, { Value: -3, duration: 30.5 }] }, { Source: "Buffout", Effects: [{ Value: 2, IsActive: false }] });
}) }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, ok); }));
await test("odd values: no extents, a quest with no objectives, a station with no frequency", () => withBoth({ tag: "odd2", game: GAME, build: modified((t) => {
  delete t.Map.World.Extents.NWX; t.Map.CurrCell = "Vault 111"; t.Quests[2].active = true; t.Quests[2].objectives = [];
  t.Radio.push({ text: "Silent Station", inRange: false, active: false }); t.Inventory[43][0].equipState = 0; delete t.Stats.HeadCondition;
}) }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, (f) => f.status === "ok"); }));
await test("main menu", () => withBoth({ tag: "menu", game: GAME, build: modified((t) => { t.Status.IsDataUnavailable = true; }) }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, (f) => f.status === "menu"); assert.equal(b.vitals, undefined); }));
await test("loading screen", () => withBoth({ tag: "loading", game: GAME, build: modified((t) => { t.Status.IsLoading = true; }) }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, (f) => f.status === "loading"); }));
await test("dead", () => withBoth({ tag: "dead", game: GAME, build: modified((t) => { t.Status.IsPlayerDead = true; }) }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, (f) => f.status === "dead"); }));
await test("connected, Pip-Boy not opened yet (nodata)", () => withBoth({ tag: "nodata", game: GAME, hold: true }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, (f) => f.status === "nodata"); }));
await test("another app has the game (busy)", () => withBoth({ tag: "busy", game: GAME, busy: true }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, (f) => f.status === "busy"); }));
await test("game running, app server off (no-app)", () => withBoth({ tag: "noapp", game: GAME, noServer: true }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, (f) => f.status === "no-app" && f.sources.pipboy.state === "refused"); }));
await test("game not running (no-game)", () => withBoth({ tag: "nogame", game: GAME, running: false }, async ({ dirA, dirB }) => { await compareFeeds(dirA, dirB, (f) => f.status === "no-game"); }));
await test("touch controls off", () => withBoth({ tag: "noctl", game: GAME, controls: false }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, ok); assert.deepEqual(b.controls, { on: false }); }));
await test("map picture off", () => withBoth({ tag: "nopic", game: GAME, picture: false }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, (f) => f.status === "ok"); assert.deepEqual(b.mapPicture, { on: false }); }));
await test("the game going away and coming back", () => withBoth({ tag: "kick", game: GAME }, async ({ dirA, dirB, fa, fb }) => {
  await compareFeeds(dirA, dirB, ok);
  fa.kick(); fb.kick();
  await compareFeeds(dirA, dirB, (f) => f.status === "ok" && f.sources.pipboy.state === "live");
}));
await test("changes as the game sends them (time, heading, health)", () => withBoth({ tag: "ticks", game: GAME }, async ({ dirA, dirB, fa, fb }) => {
  await compareFeeds(dirA, dirB, ok);
  for (let i = 0; i < 5; i++) { fa.tick(); fb.tick(); await sleep(150); }
  fa.set(["PlayerInfo", "Caps"], 99999); fb.set(["PlayerInfo", "Caps"], 99999);
  await compareFeeds(dirA, dirB, (f) => f.caps === 99999);
}));

console.log("\nthe map picture, from made-up game folders");
for (const [kind, expect] of [["loose-bc3", "ready"], ["gnrl", "ready"], ["order", "ready"], ["bc7", "the texture format BC7 is not read"], ["none", "no map texture"]]) {
  await test(`world map: ${kind}`, () => withBoth({ tag: `pic-${kind}`, game: gameDir(kind) }, async ({ dirA, dirB, wa, wb }) => {
    const { a, b } = await compareFeeds(dirA, dirB, ok);
    if (expect !== "ready") { assert.equal(b.mapPicture.world.error, expect); return expect; }
    const ra = await req(wa, "GET", "/map/world.png", { headers: { "X-Commonwealth-Deck": a.controls.key } });
    const rb = await req(wb, "GET", "/map/world.png", { headers: { "X-Commonwealth-Deck": b.controls.key } });
    assert.equal(rb.status, 200); assert.equal(rb.headers["content-type"], "image/png");
    return samePng(ra.body, rb.body, "world.png");
  }));
}
await test("world map: not a Fallout 4 folder", () => withBoth({ tag: "pic-notgame", game: newDir("empty-folder") }, async ({ dirA, dirB }) => { const { b } = await compareFeeds(dirA, dirB, ok); assert.equal(b.mapPicture.world.error, "not a Fallout 4 folder"); }));
await test("world map: the plugin's cache is the Link's cache", () => withBoth({ tag: "pic-cache", game: GAME }, async ({ dirA, dirB }) => {
  const { a, b } = await compareFeeds(dirA, dirB, ok);
  assert.equal(b.mapPicture.world.version, a.mapPicture.world.version, "the same game key");
  const ma = JSON.parse(readFileSync(join(dirA, "worldmap.json"), "utf8")), mb = JSON.parse(readFileSync(join(dirB, "worldmap.json"), "utf8"));
  assert.deepEqual(mb, ma);
  samePng(readFileSync(join(dirA, "worldmap-commonwealth.png")), readFileSync(join(dirB, "worldmap-commonwealth.png")), "cached png");
}));

console.log("\nthe widget endpoint");
await test("local map: asked for only while the widget fetches it, same picture and corners", () => withBoth({ tag: "local", game: GAME }, async ({ dirA, dirB, wa, wb, fa, fb }) => {
  let { a, b } = await compareFeeds(dirA, dirB, ok);
  await sleep(1500);
  assert.equal(fb.commands.filter((c) => c.type === 13).length, 0, "no local map asked for before the widget wants one");
  const first = await req(wb, "GET", "/map/local.png", { headers: { "X-Commonwealth-Deck": b.controls.key } });
  await req(wa, "GET", "/map/local.png", { headers: { "X-Commonwealth-Deck": a.controls.key } });
  assert.equal(first.status, 404);
  ({ a, b } = await compareFeeds(dirA, dirB, (f) => ok(f) && f.mapPicture.local));
  const ra = await req(wa, "GET", "/map/local.png", { headers: { "X-Commonwealth-Deck": a.controls.key } });
  const rb = await req(wb, "GET", "/map/local.png", { headers: { "X-Commonwealth-Deck": b.controls.key } });
  const size = samePng(ra.body, rb.body, "local.png");
  await sleep(9500);
  const asked = fb.commands.filter((c) => c.type === 13).length, askedA = fa.commands.filter((c) => c.type === 13).length;
  await sleep(3000);
  assert.equal(fb.commands.filter((c) => c.type === 13).length, asked, "stops asking when the widget stops fetching");
  return `${size}, ${asked} asks (plugin ${askedA})`;
}));
await test("commands: the same answers and the same game afterwards", () => withBoth({ tag: "commands", game: GAME }, async ({ dirA, dirB, wa, wb, fa, fb }) => {
  const { a, b } = await compareFeeds(dirA, dirB, ok);
  const steps = [
    { id: 1, name: "use", args: { item: "stimpak" } }, { id: 2, name: "radio", args: { station: "classical radio", on: true } },
    { id: 3, name: "radio", args: { station: "Classical Radio", on: true } }, { id: 4, name: "radio", args: { station: "Distress Signal" } },
    { id: 5, name: "radio", args: { station: "Nowhere FM" } }, { id: 6, name: "markHere" }, { id: 7, name: "use", args: { item: "radx" } },
    { id: 8, name: "clearMarker" }, { id: 9, name: "clearMarker" }, { id: 10, name: "use", args: { item: "radaway" } }, { id: 11, name: "use", args: { item: "radaway" } },
    { id: 12, name: "use", args: { item: "radaway" } }, { id: 13, name: "use", args: { item: "radaway" } }, { id: 14, name: "radio", args: { station: "Diamond City Radio", on: false } },
  ];
  const log = [];
  for (const s of steps) {
    const [ra, rb] = await Promise.all([command(wa, a.controls.key, s), command(wb, b.controls.key, s)]);
    assert.equal(rb.status, ra.status, `step ${s.id} ${s.name}: HTTP status`);
    assert.deepEqual(json(rb), json(ra), `step ${s.id} ${s.name}: answer`);
    log.push(`${s.id}:${json(rb).msg || json(rb).error}`);
    await sleep(700);
  }
  assert.deepEqual(fb.commands.map((c) => [c.type, c.args]), fa.commands.filter((c) => c.type !== 13).map((c) => [c.type, c.args]), "the game got the same commands");
  await compareFeeds(dirA, dirB, (f) => ok(f) && f.chems.radaway === 0);
  return log.join(" ");
}));
await test("refusals and answers: Host, Origin, key, lengths, types, rate limits", () => withBoth({ tag: "refusals", game: GAME }, async ({ dirA, dirB, wa, wb }) => {
  const { a, b } = await compareFeeds(dirA, dirB, ok);
  const K = { A: a.controls.key, B: b.controls.key };
  const cases = [
    ["hello", (p) => req(p, "GET", "/hello")],
    ["hello, 127.0.0.1", (p) => req(p, "GET", "/hello", { host: `127.0.0.1:${p}` })],
    ["hello, [::1]", (p) => req(p, "GET", "/hello", { host: `[::1]:${p}` })],
    ["hello, Origin null", (p) => req(p, "GET", "/hello", { headers: { Origin: "null" } })],
    ["hello, Origin file://", (p) => req(p, "GET", "/hello", { headers: { Origin: "file://" } })],
    ["bad Host", (p) => req(p, "GET", "/hello", { host: `evil.example:${p}` })],
    ["Host without the port", (p) => req(p, "GET", "/hello", { host: "localhost" })],
    ["Host on another port", (p) => req(p, "GET", "/hello", { host: `localhost:${p + 1}` })],
    ["no Host", (p) => req(p, "GET", "/hello", { host: null })],
    ["two Hosts", (p) => req(p, "GET", "/hello", { headers: { Host: `localhost:${p}` } })],
    ["web page Origin", (p) => req(p, "GET", "/hello", { headers: { Origin: "https://evil.example" } })],
    ["Origin file:///x", (p) => req(p, "GET", "/hello", { headers: { Origin: "file:///C:/x" } })],
    ["unknown path", (p) => req(p, "GET", "/state.json")],
    ["preflight", (p) => req(p, "OPTIONS", "/command", { headers: { "Access-Control-Request-Method": "POST" } })],
    ["GET /command", (p) => req(p, "GET", "/command")],
    ["no key", (p) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json" }, body: "{}" })],
    ["wrong key", (p) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": "0".repeat(32) }, body: "{}" })],
    ["two keys", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": [k, k] }, body: "{}" })],
    ["chunked", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": k, "Transfer-Encoding": "chunked" }, body: "2\r\n{}\r\n0\r\n\r\n" })],
    ["no length", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": k } })],
    ["bad length", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": k, "Content-Length": "1e3" } })],
    ["body over 1 KB", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": k, "Content-Length": "2000" }, body: "x".repeat(2000) })],
    ["empty body", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": k, "Content-Length": "0" } })],
    ["wrong type", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "text/html", "X-Commonwealth-Deck": k }, body: "{}" })],
  ];
  const rest = [
    ["text/plain with charset", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "text/plain; charset=utf-8", "X-Commonwealth-Deck": k }, body: JSON.stringify({ type: "command", id: 21, name: "markHere" }) })],
    ["not JSON", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": k }, body: "{nope" })],
    ["not a command", (p, k) => command(p, k, { type: "ping", id: 4 })],
    ["unknown command", (p, k) => req(p, "POST", "/command", { headers: { "Content-Type": "application/json", "X-Commonwealth-Deck": k }, body: JSON.stringify({ type: "command", id: 5, name: "fastTravel" }) })],
    ["radio, no station", (p, k) => command(p, k, { id: 6, name: "radio", args: {} })],
    ["use, unknown item", (p, k) => command(p, k, { id: 7.0, name: "use", args: { item: "jet" } })],
  ];
  const compareAll = async (list) => {
    for (const [name, fn] of list) {
      const [ra, rb] = [await fn(wa, K.A), await fn(wb, K.B)];
      const ja = json(ra), jb = json(rb);
      if (name.startsWith("hello")) { for (const j of [ja, jb]) { delete j.app; delete j.version; } }
      assert.equal(rb.status, ra.status, `${name}: HTTP status (body ${JSON.stringify(jb)})`);
      assert.deepEqual(jb, ja, `${name}: body`);
      assert.equal(!!rb.headers["access-control-allow-origin"], !!ra.headers["access-control-allow-origin"], `${name}: CORS header`);
    }
  };
  await compareAll(cases);
  await sleep(1100);
  await compareAll(rest.slice(0, 3));
  await sleep(1100);
  await compareAll(rest.slice(3));
  await sleep(1100);
  for (const port of [wa, wb]) {
    const k = port === wa ? K.A : K.B, codes = [];
    for (let i = 0; i < 6; i++) codes.push((await command(port, k, { id: 30 + i, name: "use", args: { item: "jet" } })).status);
    assert.deepEqual(codes, [400, 400, 400, 400, 400, 429], `command rate on ${port === wa ? "the plugin" : "the link"}`);
  }
  await sleep(1100);
  for (const port of [wa, wb]) {
    const k = port === wa ? K.A : K.B, codes = [];
    codes.push((await req(port, "GET", "/map/world.png")).status);
    for (let i = 0; i < 7; i++) codes.push((await req(port, "GET", "/map/world.png?v=1", { headers: { "X-Commonwealth-Deck": k } })).status);
    assert.deepEqual(codes, [403, 200, 200, 200, 200, 200, 200, 429], `picture rate on ${port === wa ? "the plugin" : "the link"}`);
  }
  const h = json(await req(wb, "GET", "/hello"));
  assert.equal(h.app, "Commonwealth Link");
  const v6 = await new Promise((res) => { const s = net.connect(wb, "::1", () => { s.end(`GET /hello HTTP/1.1\r\nHost: [::1]:${wb}\r\n\r\n`); }); let t = ""; s.on("data", (c) => { t += c; }); s.on("close", () => res(t)); s.on("error", () => res("")); });
  assert.match(v6, /^HTTP\/1\.1 200/, "answers on ::1");
  return `${cases.length + rest.length} requests alike`;
}));

console.log("\nliving alongside Commonwealth Deck");
await test("stands by while Commonwealth Deck writes the feed, takes over when it stops", async () => {
  const fake = await new FakePipboy({ port: 0, build: W.world }).start();
  const dir = newDir("standby"), wp = await freePort();
  const link = linkSide({ dir, pipPort: fake.port, widgetPort: wp, game: GAME });
  let writer = null;
  try {
    const t0 = Date.now();
    while (!(readFeed(dir) && readFeed(dir).status === "ok")) { if (Date.now() - t0 > 15000) throw new Error("the Link never got in game"); await sleep(200); }
    assert.equal(fake.clients.size, 1);
    const deckFeed = () => JSON.stringify({ feed: 1, app: "Commonwealth Deck", version: "0.4.3", seq: 1, at: Date.now(), status: "busy", statusText: "x", source: null, sources: {}, controls: { on: true, port: 52827, error: "port in use" }, mapPicture: { on: false }, end: true });
    writer = setInterval(() => { try { writeFileSync(join(dir, "feed.json"), deckFeed()); } catch { } }, 500);
    const t1 = Date.now();
    while (fake.clients.size > 0) { if (Date.now() - t1 > 8000) throw new Error("the Link kept the game's connection"); await sleep(100); }
    const refused = await new Promise((res) => { const s = net.connect(wp, "127.0.0.1"); s.on("connect", () => { s.destroy(); res(false); }); s.on("error", () => res(true)); });
    assert.ok(refused, "the Link closed its endpoint");
    await sleep(2500);
    assert.equal(readFeed(dir).app, "Commonwealth Deck", "the Link left feed.json to the plugin");
    assert.equal(fake.clients.size, 0, "and did not ask the game again");
    clearInterval(writer); writer = null;
    const t2 = Date.now();
    while (!(readFeed(dir) && readFeed(dir).app === "Commonwealth Link" && readFeed(dir).status === "ok")) { if (Date.now() - t2 > 20000) throw new Error("the Link did not take over"); await sleep(200); }
    assert.equal(fake.clients.size, 1);
    return `took over ${((Date.now() - t2) / 1000).toFixed(1)} s after the last plugin write`;
  } finally { if (writer) clearInterval(writer); await link.stop(); await fake.stop(); }
});
await test("feed.json is replaced whole, with no temp files left behind", async () => {
  const dir = newDir("files");
  const fake = await new FakePipboy({ port: 0, build: W.world, tickMs: 100 }).start();
  const link = linkSide({ dir, pipPort: fake.port, widgetPort: await freePort(), game: GAME });
  try {
    let reads = 0, bad = 0;
    const t0 = Date.now();
    while (Date.now() - t0 < 4000) { try { const t = readFileSync(join(dir, "feed.json"), "utf8"); reads++; const f = JSON.parse(t); if (f.end !== true) bad++; } catch (e) { if (e instanceof SyntaxError) bad++; } await sleep(5); }
    await link.stop();
    const left = readdirSync(dir).filter((n) => /\.tmp$|~RF/i.test(n));
    assert.equal(bad, 0, "a reader never saw half a file");
    assert.deepEqual(left, [], "no temp files");
    const f = readFileSync(join(dir, "feed.json"));
    assert.ok(!(f[0] === 0xef && f[1] === 0xbb), "no BOM");
    return `${reads} reads while it wrote`;
  } finally { await link.stop(); await fake.stop(); }
});

console.log("\nthe sort order of names");
await test("names sort as the plugin's localeCompare does", async () => {
  const pool = [..."aAbBcCeEéÉèzZ019 -_.,'()&/+æÆßœøØåñÑüÜ"];
  const names = new Set(["Stimpak", "stimpak", "RadAway", "Rad-X", "Radx", "Rad X", "Nuka-Cola", "Nuka Cola", "NukaCola", "Nuka-Cola Quantum", ".308 Round", ".45 Round", "10mm Round", "2mm Electromagnetic Cartridge", "5.56 Round", "Ætherium", "Aetherium", "Straße", "Strasse", "Élan", "elan", "Zeta", "½ Ration", "1/2 Ration", "(Bracket)", "Vault 111", "Vault 81", "Vault 8", "Mr. Handy", "Mr Handy", "Sanctuary Hills", "Sanctuary", "Café", "Cafe", "CAFE", "日本", "Ölberg", "Olberg"]);
  let seed = 7;
  const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };
  for (let i = 0; i < 1500; i++) { let s = ""; const n = 1 + Math.floor(rnd() * 8); for (let j = 0; j < n; j++) s += pool[Math.floor(rnd() * pool.length)]; names.add(s.trim() || "x"); }
  const list = [...names];
  const file = join(TMP, "names.txt"); writeFileSync(file, list.join("\n") + "\n");
  const out = await new Promise((res) => { const c = spawn(EXE, ["--sort-names", file]); let t = ""; c.stdout.on("data", (b) => { t += b; }); c.on("exit", () => res(t)); });
  const got = out.split(/\r?\n/).filter(Boolean), want = [...list].sort((a, b) => a.localeCompare(b, "en", { sensitivity: "base" }));
  const bad = want.map((w, i) => [w, got[i]]).filter(([w, g]) => w !== g);
  assert.equal(bad.length, 0, `${bad.length} of ${want.length} out of place, first: ${JSON.stringify(bad.slice(0, 5))}`);
  return `${want.length} names`;
});
await test("archives search (and hash) in the plugin's order", async () => {
  const names = ["Fallout4 - Textures1.ba2", "Fallout4 - Textures9.ba2", "Fallout4 - Textures10.ba2", "Fallout4 - TexturesPatch.ba2", "Fallout4 - Interface.ba2", "DLCUltraHighResolution - Textures01.ba2", "DLCUltraHighResolution - Textures2.ba2", "DLCUltraHighResolution - Textures16.ba2",
    "DLCRobot - Textures.ba2", "DLCRobot - Main.ba2", "dlcrobot - textures.ba2", "ccBGSFO4001-PipBoy(Black) - Textures.ba2", "ccBGSFO4044-HellfirePowerArmor - Main.ba2", "Mod_Textures.BA2", "mod_textures.ba2", "readme.txt", "Unofficial Fallout 4 Patch - Textures.ba2"];
  const real = (() => { try { const d = join("Z:", "Steam", "steamapps", "common", "Fallout 4", "Data"); return readdirSync(d); } catch { return []; } })();
  const list = [...new Set([...names, ...real])];
  const file = join(TMP, "archives.txt"); writeFileSync(file, list.join("\n") + "\n");
  const out = await new Promise((res) => { const c = spawn(EXE, ["--sort-archives", file]); let t = ""; c.stdout.on("data", (b) => { t += b; }); c.on("exit", () => res(t)); });
  assert.deepEqual(out.split(/\r?\n/).filter(Boolean), archiveOrder(list));
  return `${list.length} names${real.length ? `, ${real.length} from the installed game` : ""}`;
});

const replay = (file) => async () => {
  const bytes = readFileSync(file);
  const server = net.createServer((sock) => { sock.on("error", () => {}); sock.write(bytes); const t = setInterval(() => sock.write(keepAlive()), 1000); sock.on("close", () => clearInterval(t)); });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  return { port: server.address().port, stop: () => new Promise((r) => server.close(() => r())), kick() {} };
};
if (process.env.CAPTURES) {
  console.log("\nreal streams, replayed (local only)");
  for (const name of readdirSync(process.env.CAPTURES).filter((n) => /^pipboy-capture.*\.bin$/.test(n))) {
    await test(`replay ${name}`, () => withBoth({ tag: `replay`, game: process.env.REAL_GAME ? "" : GAME, serve: replay(join(process.env.CAPTURES, name)) }, async ({ dirA, dirB, wa, wb }) => {
      const { a, b } = await compareFeeds(dirA, dirB, (f) => f.status !== "no-game" && f.status !== "no-app" && (f.status !== "ok" || ok(f)), { ms: 90000 });
      let pic = "";
      if (b.mapPicture.world && b.mapPicture.world.version) {
        const ra = await req(wa, "GET", "/map/world.png", { headers: { "X-Commonwealth-Deck": a.controls.key } });
        const rb = await req(wb, "GET", "/map/world.png", { headers: { "X-Commonwealth-Deck": b.controls.key } });
        pic = ` (${samePng(ra.body, rb.body, "world.png")} px alike)`;
      }
      return `${b.status}, ${(b.map && b.map.markers.length) || 0} markers, ${(b.inventory && b.inventory.aid.length) || 0} aid, power armor ${b.flags && b.flags.powerArmor}, map ${b.mapPicture.world.version || b.mapPicture.world.error}${pic}`;
    }));
  }
}

console.log(`\n${pass} passed, ${fail} failed`);
writeFileSync(join(TMP, "results.json"), JSON.stringify(results, null, 1));
if (!process.env.KEEP_TMP && !fail) rmSync(TMP, { recursive: true, force: true });
process.exit(fail ? 1 : 0);

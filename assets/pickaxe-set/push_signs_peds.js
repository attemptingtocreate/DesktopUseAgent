const fs = require("fs");
const net = require("net");
const crypto = require("crypto");
const dir = "C:/Users/Administrator/Desktop/DesktopUseAgent/assets/pickaxe-set";
const SESSION = "ea5963d9-af9a-4848-a1ba-26a486b1d7f8";

function toLua(v) {
  if (v === null) return "nil";
  if (typeof v === "number") return String(v);
  if (typeof v === "boolean") return v ? "true" : "false";
  if (typeof v === "string") return JSON.stringify(v);
  if (Array.isArray(v)) return "{" + v.map(toLua).join(",") + "}";
  return (
    "{" +
    Object.keys(v)
      .map((k) => "[" + JSON.stringify(k) + "]=" + toLua(v[k]))
      .join(",") +
    "}"
  );
}

function writePkg(fileName, obj) {
  const src = "-- " + fileName + "\nreturn " + toLua(obj) + "\n";
  fs.writeFileSync(`${dir}/${fileName}.lua`, src);
  console.log(fileName, Buffer.byteLength(src));
  return src;
}

const props = {};
for (const n of ["RollButtonSystem", "SideButton", "PickaxePedestal", "RefineSign"]) {
  props[n] = JSON.parse(fs.readFileSync(`${dir}/${n}.mesh.json`, "utf8"));
}
const propsSrc = writePkg("PropsOnly.MeshPackage", props);
const up = JSON.parse(fs.readFileSync(`${dir}/UpgradeSign.mesh.json`, "utf8"));
const upSrc = writePkg("UpgradeSign.MeshPackage", up);

function push(instanceId, source) {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID().replace(/-/g, "");
    const req = {
      id,
      method: "roblox.set_script_source",
      params: { sessionId: SESSION, instanceId, source },
    };
    const s = net.createConnection("\\\\.\\pipe\\semantic-desktop-agent");
    let buf = "";
    const t = setTimeout(() => reject(new Error("timeout " + instanceId)), 120000);
    s.on("connect", () => s.write(JSON.stringify(req) + "\n"));
    s.on("data", (d) => {
      buf += d.toString();
      if (buf.includes("\n")) {
        clearTimeout(t);
        console.log(instanceId, buf.trim().slice(0, 350));
        s.end();
        resolve(JSON.parse(buf.trim()));
      }
    });
    s.on("error", reject);
  });
}

function rpc(method, params) {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID().replace(/-/g, "");
    const req = { id, method, params: { sessionId: SESSION, ...params } };
    const s = net.createConnection("\\\\.\\pipe\\semantic-desktop-agent");
    let buf = "";
    const t = setTimeout(() => reject(new Error("timeout")), 60000);
    s.on("connect", () => s.write(JSON.stringify(req) + "\n"));
    s.on("data", (d) => {
      buf += d.toString();
      if (buf.includes("\n")) {
        clearTimeout(t);
        s.end();
        resolve(JSON.parse(buf.trim()));
      }
    });
    s.on("error", reject);
  });
}

(async () => {
  // Fresh module names to avoid require cache
  const a = await rpc("roblox.create_instance", {
    className: "ModuleScript",
    name: "PropsOnlyMeshV2",
    parentPath: "ServerStorage",
  });
  const b = await rpc("roblox.create_instance", {
    className: "ModuleScript",
    name: "UpgradeSignMeshV3",
    parentPath: "ServerStorage",
  });
  const idA = a.data.id;
  const idB = b.data.id;
  console.log("created", idA, idB);
  await push(idA, propsSrc);
  await push(idB, upSrc);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});

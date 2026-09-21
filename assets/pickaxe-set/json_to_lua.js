const fs = require("fs");
const dir = "C:/Users/Administrator/Desktop/DesktopUseAgent/assets/pickaxe-set";

function toLua(v) {
  if (v === null) return "nil";
  if (typeof v === "number") return String(v);
  if (typeof v === "boolean") return v ? "true" : "false";
  if (typeof v === "string") return JSON.stringify(v);
  if (Array.isArray(v)) return "{" + v.map((x) => toLua(x)).join(",") + "}";
  return (
    "{" +
    Object.keys(v)
      .map((k) => "[" + JSON.stringify(k) + "]=" + toLua(v[k]))
      .join(",") +
    "}"
  );
}

function writePackage(name, obj) {
  const src = "-- Blender mesh package: " + name + "\nreturn " + toLua(obj) + "\n";
  const out = `${dir}/${name}.MeshPackage.lua`;
  fs.writeFileSync(out, src);
  console.log(name, Buffer.byteLength(src, "utf8"));
  return out;
}

const mainNames = [
  "RollButtonSystem",
  "SideButton",
  "PickaxePedestal",
  "RefineSign",
  "OreSimple",
  "OreTier1",
  "OreTier2",
  "OreTier3",
  "OreTier4",
  "OreTier5",
  "OreTier6",
  "OreTier7",
  "OreTier8",
  "OreTier9",
  "OreTier10",
];

const main = {};
for (const n of mainNames) {
  const p = `${dir}/${n}.mesh.json`;
  if (!fs.existsSync(p)) {
    console.warn("missing", n);
    continue;
  }
  main[n] = JSON.parse(fs.readFileSync(p, "utf8"));
}
writePackage("MeshPackages", main);
writePackage(
  "UpgradeSign",
  JSON.parse(fs.readFileSync(`${dir}/UpgradeSign.mesh.json`, "utf8"))
);

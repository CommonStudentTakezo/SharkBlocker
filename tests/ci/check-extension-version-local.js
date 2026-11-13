// Versão SEM git do verificador: guarda uma impressão digital da pasta BrowserExtension/ (sem o campo "version") junto com a
// version do manifest.json em tests/ci/.extension-state.json. Se a pasta mudou e a version NÃO subiu desde a última vez que
// passou, falha. (O Chrome só atualiza a extensão quando a versão sobe.) Primeira execução: só grava a referência.
// Uso: node tests/ci/check-extension-version-local.js        (rode da pasta do projeto)
const fs = require("fs"), path = require("path"), crypto = require("crypto");
const dir = "BrowserExtension", stateFile = path.join("tests", "ci", ".extension-state.json");

const cmp = (a, b) => {
  const pa = a.split(".").map(Number), pb = b.split(".").map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) { const d = (pa[i] || 0) - (pb[i] || 0); if (d) return d; }
  return 0;
};
const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => {
  const p = path.join(d, e.name);
  return e.isDirectory() ? walk(p) : [p];
}).sort();

const manifestPath = path.join(dir, "manifest.json");
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const version = manifest.version;
const h = crypto.createHash("sha256");
for (const f of walk(dir)) {
  let data = fs.readFileSync(f);
  if (f === manifestPath) data = Buffer.from(JSON.stringify({ ...manifest, version: "" }));
  h.update(f.replace(/\\/g, "/") + "\0").update(data).update("\0");
}
const hash = h.digest("hex");

let state = null;
try { state = JSON.parse(fs.readFileSync(stateFile, "utf8")); } catch (e) { /* primeira vez */ }
const save = () => fs.writeFileSync(stateFile, JSON.stringify({ hash, version }, null, 2) + "\n");

if (!state) { save(); console.log("Referência gravada (versão " + version + "). Daqui em diante vou avisar se a extensão mudar sem subir a versão."); process.exit(0); }
if (state.hash === hash) { console.log("BrowserExtension/ não mudou desde a última verificação (versão " + version + "): ok."); process.exit(0); }
if (cmp(version, state.version) > 0) { save(); console.log("Extensão mudou e a versão subiu " + state.version + " -> " + version + ": ok."); process.exit(0); }
console.error("ERRO: a extensão mudou, mas a version do manifest.json não subiu (continua " + version + "). Aumente a version, senão o Chrome não atualiza a extensão.");
process.exit(1);

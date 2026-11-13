// Falha se algum arquivo de BrowserExtension/ mudou desde o commit-base e a "version" do manifest.json não subiu.
// (O Chrome só atualiza a extensão quando a versão sobe: esquecer isso fez a exceção do S3 não valer em 2026-11-03.)
// Uso: BASE_SHA=<commit> node tests/ci/check-extension-version.js   (sem BASE_SHA compara com HEAD~1)
const { execFileSync } = require("child_process");

const git = (...args) => execFileSync("git", args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
const raw = process.env.BASE_SHA || "";
const base = raw && !/^0+$/.test(raw) ? raw : "HEAD~1";

const cmp = (a, b) => {
  const pa = a.split(".").map(Number), pb = b.split(".").map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d;
  }
  return 0;
};

let changed;
try {
  git("rev-parse", "--verify", base + "^{commit}");
  changed = git("diff", "--name-only", base, "HEAD", "--", "BrowserExtension").split("\n").filter(Boolean);
} catch (e) {
  console.log("Sem commit-base para comparar (" + base + "): verificação ignorada.");
  process.exit(0);
}
if (changed.length === 0) { console.log("BrowserExtension/ não mudou: ok."); process.exit(0); }

let oldVersion;
try { oldVersion = JSON.parse(git("show", base + ":BrowserExtension/manifest.json")).version; }
catch (e) { console.log("manifest.json não existia no commit-base: ok."); process.exit(0); }
const newVersion = JSON.parse(require("fs").readFileSync("BrowserExtension/manifest.json", "utf8")).version;

console.log("Arquivos alterados em BrowserExtension/: " + changed.join(", "));
if (cmp(newVersion, oldVersion) > 0) { console.log("version " + oldVersion + " -> " + newVersion + ": ok."); process.exit(0); }
console.error("ERRO: a extensão mudou, mas a version do manifest.json não subiu: agora é " + newVersion +
  " (era " + oldVersion + "). Aumente a version, senão o Chrome não atualiza a extensão.");
process.exit(1);

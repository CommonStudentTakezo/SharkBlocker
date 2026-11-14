#!/usr/bin/env python3
"""
Copia o modelo do NudeNet (segunda opinião do detector de imagens do PC) do pacote pip 'nudenet' para
App/Assets/nsfw/nudenet.onnx. O arquivo NÃO vem no projeto (sem rede no ambiente onde o projeto é montado).

Uso (no seu PC, na raiz do projeto):
    pip install nudenet
    python scripts/obter-modelo-nudenet.py

O pacote 3.x já traz o 320n.onnx (entrada 320x320, ~12 MB). Se você baixou o 640m.onnx (release v3.4-weights do GitHub do
NudeNet) e quer usá-lo, passe o caminho:  python scripts/obter-modelo-nudenet.py C:\\caminho\\640m.onnx
(o C# lê o tamanho de entrada do próprio modelo; o 640m é mais preciso e mais lento).

Licença: o pacote nudenet declara MIT, mas o modelo foi treinado com o YOLOv8 da Ultralytics (AGPL-3.0). Para uso pessoal não
há problema; antes de DISTRIBUIR o SharkBlocker com este arquivo, confira as licenças.
"""
import importlib.util
import pathlib
import shutil
import sys

DEST = pathlib.Path(__file__).resolve().parent.parent / "App" / "Assets" / "nsfw" / "nudenet.onnx"


def find_in_package() -> pathlib.Path:
    # find_spec não importa o pacote (não puxa o onnxruntime só para localizar o arquivo).
    spec = importlib.util.find_spec("nudenet")
    if spec is None or not spec.submodule_search_locations:
        sys.exit("O pacote 'nudenet' não está instalado. Rode:  pip install nudenet")
    pkg = pathlib.Path(list(spec.submodule_search_locations)[0])
    found = sorted(pkg.glob("*.onnx"))
    preferred = [f for f in found if f.name == "320n.onnx"] or found
    if not preferred:
        sys.exit(f"Nenhum .onnx dentro de {pkg}. Baixe o modelo e passe o caminho como argumento.")
    return preferred[0]


def main() -> None:
    src = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else find_in_package()
    if not src.is_file():
        sys.exit(f"Arquivo não encontrado: {src}")
    DEST.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(src, DEST)
    print(f"Copiado: {src}  ->  {DEST}  ({DEST.stat().st_size / 1_000_000:.1f} MB)")
    print("Reabra o SharkBlocker (ou rode quick-install.ps1): a aba Proteções deve mostrar 'NudeNet (segunda opinião) pronto.'")


if __name__ == "__main__":
    main()

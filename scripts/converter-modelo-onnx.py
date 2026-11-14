# -*- coding: utf-8 -*-
"""
Converte o modelo do GantMan (mobilenet_v2_140_224; classes drawings/hentai/neutral/porn/sexy) de frozen_graph.pb para ONNX,
SEM precisar de TensorFlow: so numpy + protobuf (+ onnxruntime para a checagem final).

Uso (PowerShell, na pasta do projeto do PC):
    py -3.11 -m venv venv-modelo
    .\\venv-modelo\\Scripts\\activate
    pip install numpy protobuf onnxruntime
    python scripts\\converter-modelo-onnx.py C:\\caminho\\mobilenet_v2_140_224

O argumento e a PASTA mobilenet_v2_140_224 extraida do zip (a que tem frozen_graph.pb). Grava App\\Assets\\nsfw\\model.onnx e model.json.
Confira a LICENCA do modelo antes de usar.
"""
import json
import pathlib
import sys

import numpy as np
from google.protobuf import descriptor_pb2, descriptor_pool, message_factory

F = descriptor_pb2.FieldDescriptorProto
O, R = F.LABEL_OPTIONAL, F.LABEL_REPEATED


def _build(proto_name, messages):
    fd = descriptor_pb2.FileDescriptorProto()
    fd.name = proto_name
    fd.package = proto_name.split(".")[0]
    fd.syntax = "proto2" if proto_name.startswith("onnxmini") else "proto3"
    for name, fields, nested in messages:
        m = fd.message_type.add()
        m.name = name
        for (n, num, typ, label, tn) in fields:
            f = m.field.add()
            f.name, f.number, f.type, f.label = n, num, typ, label
            if tn:
                f.type_name = tn
        for nn, kt, vt, vtn in nested:  # entradas de mapa
            e = m.nested_type.add()
            e.name = nn
            e.options.map_entry = True
            k = e.field.add(); k.name = "key"; k.number = 1; k.type = kt; k.label = O
            v = e.field.add(); v.name = "value"; v.number = 2; v.type = vt; v.label = O
            if vtn:
                v.type_name = vtn
    pool = descriptor_pool.DescriptorPool()
    pool.Add(fd)
    return pool, fd.package


# --- TensorFlow GraphDef minimo (so o que o frozen_graph.pb usa) ---
_tf_pool, _tf = _build("tfmini.proto", [
    ("Dim", [("size", 1, F.TYPE_INT64, O, None)], []),
    ("Shape", [("dim", 2, F.TYPE_MESSAGE, R, ".tfmini.Dim")], []),
    ("Tensor", [("dtype", 1, F.TYPE_INT32, O, None), ("shape", 2, F.TYPE_MESSAGE, O, ".tfmini.Shape"),
                ("content", 4, F.TYPE_BYTES, O, None), ("float_val", 5, F.TYPE_FLOAT, R, None),
                ("int_val", 7, F.TYPE_INT32, R, None)], []),
    ("ListValue", [("s", 2, F.TYPE_BYTES, R, None), ("i", 3, F.TYPE_INT64, R, None)], []),
    ("Attr", [("list", 1, F.TYPE_MESSAGE, O, ".tfmini.ListValue"), ("s", 2, F.TYPE_BYTES, O, None),
              ("f", 4, F.TYPE_FLOAT, O, None), ("tensor", 8, F.TYPE_MESSAGE, O, ".tfmini.Tensor")], []),
    ("Node", [("name", 1, F.TYPE_STRING, O, None), ("op", 2, F.TYPE_STRING, O, None),
              ("input", 3, F.TYPE_STRING, R, None), ("attr", 5, F.TYPE_MESSAGE, R, ".tfmini.Node.AttrEntry")],
     [("AttrEntry", F.TYPE_STRING, F.TYPE_MESSAGE, ".tfmini.Attr")]),
    ("Graph", [("node", 1, F.TYPE_MESSAGE, R, ".tfmini.Node")], []),
])
TFGraph = message_factory.GetMessageClass(_tf_pool.FindMessageTypeByName("tfmini.Graph"))

# --- ONNX minimo (so o necessario para gravar o modelo) ---
_ox_pool, _ox = _build("onnxmini.proto", [
    ("Dimension", [("dim_value", 1, F.TYPE_INT64, O, None), ("dim_param", 2, F.TYPE_STRING, O, None)], []),
    ("TensorShape", [("dim", 1, F.TYPE_MESSAGE, R, ".onnxmini.Dimension")], []),
    ("TensorType", [("elem_type", 1, F.TYPE_INT32, O, None), ("shape", 2, F.TYPE_MESSAGE, O, ".onnxmini.TensorShape")], []),
    ("Type", [("tensor_type", 1, F.TYPE_MESSAGE, O, ".onnxmini.TensorType")], []),
    ("ValueInfo", [("name", 1, F.TYPE_STRING, O, None), ("type", 2, F.TYPE_MESSAGE, O, ".onnxmini.Type")], []),
    ("Tensor", [("dims", 1, F.TYPE_INT64, R, None), ("data_type", 2, F.TYPE_INT32, O, None),
                ("name", 8, F.TYPE_STRING, O, None), ("raw_data", 9, F.TYPE_BYTES, O, None)], []),
    ("Attribute", [("name", 1, F.TYPE_STRING, O, None), ("f", 2, F.TYPE_FLOAT, O, None), ("i", 3, F.TYPE_INT64, O, None),
                   ("s", 4, F.TYPE_BYTES, O, None), ("ints", 8, F.TYPE_INT64, R, None),
                   ("type", 20, F.TYPE_INT32, O, None)], []),
    ("Node", [("input", 1, F.TYPE_STRING, R, None), ("output", 2, F.TYPE_STRING, R, None), ("name", 3, F.TYPE_STRING, O, None),
              ("op_type", 4, F.TYPE_STRING, O, None), ("attribute", 5, F.TYPE_MESSAGE, R, ".onnxmini.Attribute")], []),
    ("Graph", [("node", 1, F.TYPE_MESSAGE, R, ".onnxmini.Node"), ("name", 2, F.TYPE_STRING, O, None),
               ("initializer", 5, F.TYPE_MESSAGE, R, ".onnxmini.Tensor"),
               ("input", 11, F.TYPE_MESSAGE, R, ".onnxmini.ValueInfo"),
               ("output", 12, F.TYPE_MESSAGE, R, ".onnxmini.ValueInfo")], []),
    ("OpsetId", [("domain", 1, F.TYPE_STRING, O, None), ("version", 2, F.TYPE_INT64, O, None)], []),
    ("Model", [("ir_version", 1, F.TYPE_INT64, O, None), ("producer_name", 2, F.TYPE_STRING, O, None),
               ("graph", 7, F.TYPE_MESSAGE, O, ".onnxmini.Graph"),
               ("opset_import", 8, F.TYPE_MESSAGE, R, ".onnxmini.OpsetId")], []),
])
OX = {n: message_factory.GetMessageClass(_ox_pool.FindMessageTypeByName("onnxmini." + n))
      for n in ["Model", "Graph", "Node", "Tensor", "Attribute", "ValueInfo", "OpsetId"]}


# ----------------------------- leitura do grafo TF -----------------------------
def load_graph(path):
    g = TFGraph()
    g.ParseFromString(open(path, "rb").read())
    return {n.name: n for n in g.node}


def tf_const(node):
    t = node.attr["value"].tensor
    dt = {1: np.float32, 3: np.int32, 9: np.int64}[t.dtype]
    shp = [d.size for d in t.shape.dim]
    if t.content:
        a = np.frombuffer(t.content, dtype=dt).copy()
    elif t.float_val:
        a = np.array(t.float_val, dtype=dt)
    else:
        a = np.array(t.int_val, dtype=dt)
    n = int(np.prod(shp)) if shp else 1
    if a.size == 1 and n > 1:
        a = np.full(n, a[0], dtype=dt)
    return a.reshape(shp) if shp else a.reshape(())


def resolve(nodes, name):
    name = name.lstrip("^").split(":")[0]
    while nodes[name].op == "Identity":
        name = nodes[name].input[0].lstrip("^").split(":")[0]
    return name


def ints(node, key):
    return list(node.attr[key].list.i)


# ----------------------------- escrita do ONNX -----------------------------
class Builder:
    def __init__(self):
        self.nodes, self.inits, self.n = [], [], 0

    def tensor(self, name, arr):
        t = OX["Tensor"]()
        name = f"{name}_{len(self.inits)}"  # nomes sempre unicos
        t.name = name
        arr = np.ascontiguousarray(arr)
        t.dims.extend(arr.shape)
        t.data_type = {np.dtype("float32"): 1, np.dtype("int64"): 7}[arr.dtype]
        t.raw_data = arr.tobytes()
        self.inits.append(t)
        return name

    def node(self, op, inputs, out=None, **attrs):
        self.n += 1
        out = out or f"t{self.n}"
        nd = OX["Node"]()
        nd.op_type, nd.name = op, f"{op}_{self.n}"
        nd.input.extend(inputs)
        nd.output.append(out)
        for k, v in attrs.items():
            a = nd.attribute.add()
            a.name = k
            if isinstance(v, float):
                a.type, a.f = 1, v
            elif isinstance(v, int):
                a.type, a.i = 2, v
            elif isinstance(v, str):
                a.type, a.s = 3, v.encode()
            else:
                a.type = 7
                a.ints.extend(v)
        self.nodes.append(nd)
        return out


def convert(frozen_path, out_path):
    nodes = load_graph(frozen_path)
    b = Builder()
    done = {}  # nome TF (resolvido) -> nome do tensor ONNX (NCHW)

    # entrada: NHWC [N,224,224,3] float32 em 0..1 -> NCHW
    done[[n for n in nodes if nodes[n].op == "Placeholder"][0]] = b.node("Transpose", ["input"], perm=[0, 3, 1, 2])

    def emit(name):
        name = resolve(nodes, name)
        if name in done:
            return done[name]
        nd = nodes[name]
        ins = [resolve(nodes, i) for i in nd.input if not i.startswith("^")]
        op = nd.op
        if op == "Const":
            raise RuntimeError("Const inesperada como tensor de dados: " + name)
        if op in ("Mul", "Sub", "AddV2"):
            xs = []
            for i in ins:
                if nodes[i].op == "Const":
                    c = tf_const(nodes[i]).astype(np.float32)
                    if c.size != 1:
                        raise RuntimeError("constante nao escalar em " + name)
                    xs.append(b.tensor(f"c_{b.n}_{len(xs)}", c.reshape(())))
                else:
                    xs.append(emit(i))
            out = b.node({"Mul": "Mul", "Sub": "Sub", "AddV2": "Add"}[op], xs)
        elif op == "Conv2D":
            w = tf_const(nodes[ins[1]]).astype(np.float32)  # HWIO
            assert nd.attr["padding"].s == b"SAME" and nd.attr["data_format"].s == b"NHWC"
            st = ints(nd, "strides")
            wn = b.tensor(f"w_{b.n}", w.transpose(3, 2, 0, 1))
            out = b.node("Conv", [emit(ins[0]), wn], kernel_shape=[w.shape[0], w.shape[1]],
                         strides=[st[1], st[2]], auto_pad="SAME_UPPER", group=1)
        elif op == "DepthwiseConv2dNative":
            w = tf_const(nodes[ins[1]]).astype(np.float32)  # H,W,C,M (M=1)
            assert w.shape[3] == 1 and nd.attr["padding"].s == b"SAME"
            st = ints(nd, "strides")
            wn = b.tensor(f"w_{b.n}", w.transpose(2, 3, 0, 1))  # C,1,H,W
            out = b.node("Conv", [emit(ins[0]), wn], kernel_shape=[w.shape[0], w.shape[1]],
                         strides=[st[1], st[2]], auto_pad="SAME_UPPER", group=int(w.shape[2]))
        elif op == "FusedBatchNormV3":
            sc, of, me, va = [b.tensor(f"bn_{b.n}_{k}", tf_const(nodes[ins[k]]).astype(np.float32)) for k in (1, 2, 3, 4)]
            out = b.node("BatchNormalization", [emit(ins[0]), sc, of, me, va], epsilon=float(nd.attr["epsilon"].f))
        elif op == "Relu6":
            lo = b.tensor(f"lo_{b.n}", np.array(0.0, dtype=np.float32))
            hi = b.tensor(f"hi_{b.n}", np.array(6.0, dtype=np.float32))
            out = b.node("Clip", [emit(ins[0]), lo, hi])
        elif op == "AvgPool":
            k = ints(nd, "ksize")
            out = b.node("AveragePool", [emit(ins[0])], kernel_shape=[k[1], k[2]], strides=[1, 1])
        elif op == "Squeeze":
            assert ints(nd, "squeeze_dims") == [1, 2]
            out = b.node("Flatten", [emit(ins[0])], axis=1)  # [N,C,1,1] -> [N,C]
        elif op == "MatMul":
            assert not nd.attr["transpose_a"].s and nodes[ins[1]].op == "Const"
            w = tf_const(nodes[ins[1]]).astype(np.float32)
            out = b.node("MatMul", [emit(ins[0]), b.tensor(f"mm_{b.n}", w)])
        elif op == "BiasAdd":
            out = b.node("Add", [emit(ins[0]), b.tensor(f"bias_{b.n}", tf_const(nodes[ins[1]]).astype(np.float32))])
        elif op == "Softmax":
            out = b.node("Softmax", [emit(ins[0])], axis=-1)
        else:
            raise RuntimeError(f"operador nao suportado: {op} ({name})")
        done[name] = out
        return out

    final = [n for n in nodes if nodes[n].op == "Softmax"][0]
    result = emit(final)
    b.node("Identity", [result], out="prediction")

    g = OX["Graph"]()
    g.name = "nsfw_mobilenet_v2_140_224"
    g.node.extend(b.nodes)
    g.initializer.extend(b.inits)
    vi_in, vi_out = g.input.add(), g.output.add()
    vi_in.name = "input"
    tt = vi_in.type.tensor_type
    tt.elem_type = 1
    d = tt.shape.dim.add(); d.dim_param = "N"
    for v in (224, 224, 3):
        tt.shape.dim.add().dim_value = v
    vi_out.name = "prediction"
    tt = vi_out.type.tensor_type
    tt.elem_type = 1
    d = tt.shape.dim.add(); d.dim_param = "N"
    tt.shape.dim.add().dim_value = 5
    m = OX["Model"]()
    m.ir_version = 8
    m.producer_name = "sharkblocker-converter"
    m.graph.CopyFrom(g)
    op = m.opset_import.add(); op.domain = ""; op.version = 13
    pathlib.Path(out_path).write_bytes(m.SerializeToString())
    return len(b.nodes)


def main():
    if len(sys.argv) != 2:
        sys.exit("Uso: python scripts\\converter-modelo-onnx.py C:\\caminho\\mobilenet_v2_140_224")
    pasta = pathlib.Path(sys.argv[1])
    frozen = pasta / "frozen_graph.pb"
    if not frozen.is_file():
        sys.exit("Nao achei frozen_graph.pb em %s (aponte para a pasta mobilenet_v2_140_224 extraida)." % pasta)
    destino = pathlib.Path(__file__).resolve().parent.parent / "App" / "Assets" / "nsfw"
    destino.mkdir(parents=True, exist_ok=True)
    saida = destino / "model.onnx"
    print("Convertendo...")
    n = convert(frozen, saida)
    print("ONNX gravado:", saida, "(%d nos, %.1f MB)" % (n, saida.stat().st_size / 1e6))

    import onnxruntime as ort
    sess = ort.InferenceSession(str(saida), providers=["CPUExecutionProvider"])
    i0, o0 = sess.get_inputs()[0], sess.get_outputs()[0]
    print("Entrada:", i0.name, i0.shape, i0.type, "| Saida:", o0.name, o0.shape)
    x = np.zeros((1, 224, 224, 3), dtype=np.float32)
    y = sess.run(None, {i0.name: x})[0]
    print("Imagem preta -> [drawings, hentai, neutral, porn, sexy] =", np.round(y, 3).tolist())
    if y.shape != (1, 5) or abs(float(y.sum()) - 1.0) > 1e-3:
        sys.exit("Saida inesperada: o modelo nao foi gravado corretamente.")
    (destino / "model.json").write_text(json.dumps({"scale": 0.00392157, "mean": 0.0, "explicitIndices": [1, 3], "suggestiveIndices": [4]}, indent=2),
                                        encoding="utf-8")
    print("model.json gravado. Proximo passo: dotnet build -c Release e conferir 'Modelo carregado e pronto'.")


if __name__ == "__main__":
    main()

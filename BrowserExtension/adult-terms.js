// Termos de conteúdo adulto — ÚNICA fonte da lista (a extensão a usa mesmo com o app fora do ar).
// Correspondência por PALAVRA INTEIRA e sem acento/maiúscula ("nude" não casa com "denuded").
// Edite à vontade; depois de editar, recarregue a extensão (ou deixe o app regerar o .crx).

// Checados na URL inteira (domínio, caminho e busca) E nas buscas digitadas.
// Conservadores de propósito: quase só aparecem em contexto adulto.
const CTC_URL_TERMS = [
  "porn", "pr0n", "porno", "pornô", "pornografia", "pornhub", "xvideos", "xnxx", "xhamster", "redtube", "youporn",
  "onlyfans", "hentai", "nsfw", "camgirl", "sexo explícito", "nudes", "xxx", "punheta",
  "brasileirinhas", "erome", "rule34", "sexcam", "sex video", "sex videos",
  // variações de "porn"
  "pornos", "pornôs", "pornstar", "pornstars", "porn star", "porn stars", "pornographic", "pornography",
  "pornográfico", "pornografico", "pornográficos", "pornograficos", "pornhd", "pornpics", "porngif", "porngifs",
  // sites e plataformas conhecidos
  "xvideo", "xhamsterlive", "spankbang", "eporner", "tnaflix", "tube8", "youjizz", "pornone", "porntrex",
  "hqporner", "thumbzilla", "fapello", "fansly", "manyvids", "clips4sale", "chaturbate", "stripchat", "bongacams",
  "livejasmin", "camsoda", "myfreecams", "imlive", "flirt4free", "nhentai", "hanime", "ehentai", "e hentai", "gelbooru",
  "brazzers", "bangbros", "realitykings", "naughtyamerica", "mofos", "pichunter", "hotmovies", "adultfriendfinder",
  "rule 34", "r34", "gonewild", "simpcity", "sexyhub", "xxxvideos", "xxx videos", "xxxtube",
  // atos e categorias explícitas (não têm sentido inocente)
  "blowjob", "blowjobs", "handjob", "footjob", "gangbang", "creampie", "bukkake", "cumshot", "cumshots", "deepthroat",
  "milf", "milfs", "bdsm", "cuckold", "dildo", "dildos", "fleshlight", "sexshop", "sex shop", "sexshops", "camwhore",
  "camwhores", "upskirt", "creepshot", "creepshots", "titties", "suruba", "surubas", "putinha",
  "putinhas", "xereca", "xoxota", "piroca", "siririca", "gozada", "gozadas", "fodendo", "bucetinha", "bucetuda",
  "peituda", "peitudas", "ninfeta", "ninfetas", "ninfetinha", "lolicon", "shotacon", "ahegao",
  "ecchi", "doujinshi", "lewd", "lewds", "nudes vazados", "nudes vazadas", "vazou nudes", "leaked nudes",
  "onlyfans leaks", "onlyfans leaked", "leaked onlyfans", "pornografía", "pornografia gratis",
  // restaurados + mais
  "orgia", "orgias", "rabuda", "rabudas", "coomer", "redgifs", "scrolller", "nudevista", "thothub", "fapodrop",
  "hentaihaven", "hentaicloud", "hentaila", "hentaiplay", "tsumino", "fakku", "sexlikereal", "javhd", "hdzog",
  "vporn", "drtuber", "hclips", "porngo", "sunporno", "porndig", "fapality", "pornhat", "cumlouder", "camwhorebay",
  "boobs", "tits", "pussy", "dick pic", "dick pics", "nude selfies", "sexting", "sext", "masturbation",
  "masturbating", "masturbate", "masturbar", "masturbacao", "bronha", "tesao", "ninfomaniaca", "buceta",
  "bucetas", "bucetao", "transar", "transando", "transaram", "transou", "fazendo sexo", "fazer sexo",
  // biquíni / bikini e variações (pedido do usuário, 2026-11-09): palavra inteira, sem acento/maiúscula; plural listado à parte
  "biquini", "biquíni", "biquinis", "biquínis", "bikini", "bikíni", "bikinis", "bikínis",
  // Playboy (revista/TV/site) e nomes de atrizes/atores de filmes adultos (pedido do usuário, 2026-11-10).
  // Só frases/nomes COMPLETOS (nunca primeiro nome sozinho); "playboy" sozinho NÃO entra (Playboy Carti, "playboy" como adjetivo).
  "playboy magazine", "playboy com", "revista playboy", "playboy brasil", "playboy plus", "playboy tv", "playboy playmate",
  "playmate of the month", "playmate of the year", "playboy nude", "playboy nudes", "playboy models", "playboy centerfold",
  "mia khalifa", "sunny leone", "riley reid", "lana rhoades", "abella danger", "mia malkova", "jenna jameson", "sasha grey",
  "asa akira", "eva elfie", "kendra lust", "brandi love", "nicole aniston", "adriana chechik", "alexis texas",
  "bonnie rotten", "dani daniels", "elsa jean", "gianna michaels", "johnny sins", "lexi belle", "madison ivy", "jesse jane",
  "tori black", "stoya", "belle delphine", "kira noir", "jada stevens", "august ames", "janice griffith", "remy lacroix",
  "phoenix marie", "peta jensen", "tera patrick", "ron jeremy", "sophie dee", "cory chase", "alina lopez", "lena paul",
  "autumn falls", "blake blossom", "violet myers", "valentina nappi", "anissa kate", "aletta ocean", "nina hartley",
  "chanel preston", "kagney linn karter", "kid bengala", "rita cadillac", "elisa sanches",
  "порно", "порнуха", "секс", "хентай", "эротика", "голая", "голые",
  "エロ", "アダルト", "無修正", "ポルノ", "ヌード", "sesso", "tette", "tetas", "tetona", "tetonas", "coño", "follar", "sexe",
];

// Checados SÓ no texto de buscas (o que você digita num buscador/YouTube), onde a intenção é clara.
// Aqui entram expressões mais amplas — como "conteúdo adulto".
const CTC_SEARCH_TERMS = [
  "conteúdo adulto", "conteudo adulto", "adult content", "conteúdo +18", "conteudo +18", "conteúdo 18+",
  "vídeo adulto", "vídeos adultos", "video adulto", "videos adultos", "filme adulto", "filmes adultos",
  "site adulto", "sites adultos", "adult video", "adult videos", "adult site", "adult sites", "adult movie", "adult movies",
  "erótico", "erótica", "eróticos", "eróticas", "erotico", "erotica", "erotic", "erotismo",
  "naked", "pelada", "peladas", "pelado", "nua", "nuas", "nu artístico", "nudez", "nude", "sexo grátis", "sexo gratis",
  "sexo ao vivo", "sexo anal", "sexo oral", "sexo amador", "vídeo de sexo", "vídeos de sexo", "video de sexo", "videos de sexo",
  "cenas de sexo", "gostosa", "gostosas", "buceta", "safada", "safadas", "novinha pelada", "mulher pelada",
  "strip tease", "striptease", "camgirls", "live sex", "sex cam", "sex chat", "sex tape",
  // pornografia (formas curtas/gírias de busca)
  "pron", "porn pics", "porn gifs", "free porn", "sexo grupal", "sexo em grupo", "sexo selvagem", "sexo explicito",
  "sex movies", "sex movie", "sex pics", "sex photos", "sex stories", "sex scenes", "hot sex", "anal sex", "oral sex",
  "group sex", "wet pussy", "pussy licking", "hot pussy", "nude pics", "nude photos", "nude girls", "nude women",
  "nude cam", "nude cams", "horny", "horny girls", "slut", "sluts", "webcam sex",
  "webcam girls", "web cam girls", "cam girls", "cam show", "cam shows", "live cam girls", "sex cams",
  "stripper", "strippers", "strip club", "strip clubs", "lap dance", "lapdance", "foot fetish", "fetish porn",
  "erotic massage", "sensual massage", "tantric massage", "escort girls", "call girl", "call girls",
  "adult chat", "adult cam", "adult cams", "adult dating", 
  // português
  "mulher nua", "mulheres nuas", "homem nu", "homens nus", "garota nua", "garotas nuas", "menina nua",
  "fotos nuas", "foto nua", "fotos de nudes", "foto de nude", "fotos intimas", "foto intima", "video intimo",
  "videos intimos", "massagem erotica", "massagem sensual", "garota de programa", "garotas de programa",
  "novinha", "novinhas", "trepando", "gostosona", "gostosinha", "safadinha", "safadinhas", "putaria", "mulheres peladas", "gatas peladas", "novinhas peladas", "peladinha", "peladinhas",
  // espanhol / outros
  "porno gratis", "videos porno", "video porno", "videos xxx", "desnuda", "desnudas", "desnudo", "desnudos",
  "follando", "pornografía", "chicas desnudas", "mujeres desnudas", "nackt", "nackte", "nacktbilder",
  "pornographie", "film porno", "filme porno", "porno gratuit",
  // restaurados + bare words (máxima cobertura nas BUSCAS)
  "sex", "sexo", "sexo com", "sexo na", "sexo no", "sexo em", "sexo casual", "casal fazendo", "transando",
  "chupando", "mamada", "nudity", "topless", "one night stand", "hookup sites", "hookup site", "hook up sites",
  "naked women", "naked girls", "naked men", "nude men", "nudes leaked", "hot girls nude", "sexy nude",
  "dar o cu", "dar a bunda", "fotos sensuais", "foto sensual", "videos sensuais", "mulher gostosa", "mulheres gostosas",
  "namorada pelada", "esposa pelada", "casada pelada", "tia pelada", "prima pelada", "morena pelada", "loira pelada",
  "sexo anal gratis", "sexo oral gratis", "sexo ao vivo gratis", "sexo na webcam", "sexo por video",
  "sexo por webcam", "sexo virtual", "chat de sexo", "bate papo erotico", "bate papo sexo", "conto erotico",
  "contos eroticos", "conto de sexo", "contos de sexo", "historias eroticas", "historia erotica",
  "sex story", "erotic stories", "erotic story", "sex chat room", "sex roleplay", "sexual roleplay",
  "jerk off", "jerking off", "wank", "wanking", "cumming", "orgasm videos", "female orgasm videos",
  "naked selfies", "nude selfie", "nude leaks", "celebrity nudes", "celeb nudes", "celebrity nude", "fake nudes",
  "deepfake porn", "deepfake nudes", "ai porn", "ai nudes", "ai hentai", "undress ai", "nudify", "nudify ai",
  "xxx ai", "porn ai", "sexo ia", "pelada ia", "nude ai",
];

// Termos FORTES: casam como TRECHO dentro de QUALQUER parte da URL (domínio colado, caminho, busca),
// por isso só entram nomes bem característicos — nada que apareça em palavras comuns.
// Pega "megaxvideosbr.com", "assistirpornhub.net", "hentaihaven.org", "freepornvideos.tv"...
// (não colocamos "porn" sozinho de propósito: bloquearia sites de recuperação do vício.)
const CTC_STRONG_TERMS = [
  "pornhub", "pornub", "xvideos", "xvideo", "xnxx", "xhamster", "redtube", "youporn", "onlyfans", "hentai",
  "brasileirinhas", "chaturbate", "stripchat", "bongacams", "livejasmin", "camsoda", "myfreecams", "spankbang",
  "eporner", "tnaflix", "youjizz", "porntrex", "hqporner", "thumbzilla", "fapello", "fansly", "manyvids",
  "clips4sale", "nhentai", "hanime", "gelbooru", "rule34", "brazzers", "bangbros", "realitykings",
  "naughtyamerica", "pornpics", "pichunter", "putaria", "camgirl", "sexcam", "sexshop", "freeporn", "pornvideo",
  "pornsite", "pornstar", "porntube", "pornotube", "sexvideo", "xxxvideo", "hotporn", "bestporn", "pornpic",
  "porngif", "nsfw", "gonewild", "redgifs", "scrolller", "nudevista", "thothub", "hentaihaven", "hentaicloud",
  "hentaila", "hentaiplay", "tsumino", "fakku", "sexlikereal", "javhd", "hdzog", "vporn", "drtuber", "hclips",
  "porngo", "sunporno", "porndig", "fapality", "pornhat", "cumlouder", "camwhore", "nudify", "undressai",
  "deepfakeporn", "sexvideos", "sexchat", "sexcams", "freesex", "nakedgirls", "nudegirls", "xxxporn",
];

// Terminações de domínio exclusivamente adultas (".xxx", ".porn", ".adult", ".sex").
const CTC_ADULT_TLDS = ["xxx", "porn", "porno", "adult", "sex", "sexy"];

// Sites BLOQUEADOS SEMPRE (com proteções permanentes ou bloqueio ativo), sem depender de bloqueio temporário.
// Domínio e todos os subdomínios. YouTube NÃO está aqui: só os Shorts (/shorts) são barrados, por regra de caminho.
// music.youtube.com é liberado (ver CTC_ALWAYS_ALLOWED_HOSTS em background.js). googlevideo.com e ytimg.com também ficam livres.
// Lido por C# (TermLists.PermanentSites) e Kotlin (TermLists.kt) pelo nome da constante; recopie este arquivo para o Android.
const CTC_PERMANENT_SITES = [
  "duckduckgo.com", "html.duckduckgo.com", "safe.duckduckgo.com", "start.duckduckgo.com", "tor.com", "onion.com",
  "onionrouter.com", "9minecraft.com", "duck.com", "facebook.com", "m.facebook.com", "mbasic.facebook.com",
  "web.facebook.com", "touch.facebook.com", "fb.com", "fb.watch", "messenger.com", "reddit.com",
  "old.reddit.com", "new.reddit.com", "m.reddit.com", "i.reddit.com", "np.reddit.com", "sh.reddit.com",
  "redd.it", "redditmedia.com", "redditstatic.com", "tiktok.com", "m.tiktok.com", "vm.tiktok.com",
  "vt.tiktok.com", "tiktokv.com", "tiktokcdn.com", "tiktokcdn-us.com", "musical.ly", "instagram.com",
  "m.instagram.com", "instagr.am", "ig.me", "cdninstagram.com", "telegram.org", "web.telegram.org",
  "webk.telegram.org", "webz.telegram.org", "t.me", "telegram.me", "telegra.ph", "x.com",
  "mobile.x.com", "twitter.com", "mobile.twitter.com", "t.co", "twimg.com", "threads.com",
  "threads.net", "snapchat.com", "web.snapchat.com", "snap.com", "sc-cdn.net", "bitmoji.com",
  "pinterest.com", "br.pinterest.com", "pin.it", "pinimg.com", "linkedin.com", "m.linkedin.com",
  "lnkd.in", "licdn.com", "twitch.tv", "m.twitch.tv", "clips.twitch.tv", "ttvnw.net",
  "jtvnw.net", "kwai.com", "kwai.net", "k.kwai.com", "bsky.app", "bsky.social",
  "tumblr.com", "m.tumblr.com", "netflix.com", "disneyplus.com", "primevideo.com", "max.com",
  "hbomax.com", "paramountplus.com", "globoplay.globo.com", "crunchyroll.com", "vimeo.com", "dailymotion.com",
  "quora.com", "4chan.org", "9gag.com", "imgur.com", "ifunny.co", "knowyourmeme.com",
  "lemmy.world", "medium.com", "poki.com", "crazygames.com", "y8.com", "kongregate.com",
  "itch.io", "minijuegos.com", "kick.com", "rumble.com", "odysee.com", "trovo.live",
];

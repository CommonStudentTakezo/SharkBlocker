// Script da página de bloqueio. Fica em arquivo próprio porque extensões Manifest V3 NÃO
// permitem <script> inline (CSP script-src 'self'): inline = bloqueado, e a página ficava sem
// texto do motivo, sem timer e sem log.

// Pegar parâmetros da URL
const params = new URLSearchParams(window.location.search);
const reason = params.get('reason') || 'unknown';
const timestamp = params.get('timestamp') || Date.now();

// Configurar mensagem baseada no tipo de bloqueio
const reasonTexts = {
    'url': '❌ SITE BLOQUEADO',
    'keyword': '⚠️ PESQUISA SUSPEITA',
    'suspicious_search': '⚠️ PESQUISA SUSPEITA DETECTADA',
    'adult': '🚫 CONTEÚDO ADULTO BLOQUEADO',
    'proxy': '🛡️ PROXY / VPN / NAVEGADOR REMOTO BLOQUEADO',
    'keyword_search': '⚠️ TERMO PROIBIDO NA BUSCA',
    'gateway': '🛡️ TRADUTOR / CACHE / ARQUIVO WEB BLOQUEADO',
    'ip': '🛡️ ACESSO POR IP BLOQUEADO',
    'unknown': '🚫 ACESSO NEGADO'
};

const reasonDescriptions = {
    'url': 'O site que você tentou acessar está bloqueado.',
    'keyword': 'Conteúdo suspeito foi detectado na página.',
    'suspicious_search': 'Você tentou buscar por conteúdo restrito.',
    'adult': 'Este endereço ou esta busca contém termos de conteúdo adulto. A tela será travada por 30 minutos.',
    'proxy': 'Este endereço ou busca parece um proxy, VPN ou navegador remoto, usado para contornar o bloqueio. A tela será travada por 30 minutos.',
    'keyword_search': 'Você buscou um termo da sua lista de proibidos. A tela será travada por 30 minutos.',
    'gateway': 'Tradutores de página, caches de buscador e arquivos web mostram outro site por dentro deles, o que contorna o bloqueio. Ficam bloqueados enquanto o bloqueio estiver ativo.',
    'ip': 'Acessar um site direto pelo número de IP contorna o bloqueio. Durante o bloqueio, só endereços locais (localhost e rede interna) abrem por IP.',
    'unknown': 'Sua solicitação foi bloqueada por proteção de segurança.'
};

// Atualizar textos
document.getElementById('reason').textContent = reasonTexts[reason] || reasonTexts['unknown'];
document.querySelector('.message').innerHTML = `
    <p>${reasonDescriptions[reason] || reasonDescriptions['unknown']}</p>
`;

// Timestamp
const date = new Date(parseInt(timestamp));
document.getElementById('timestamp').textContent = 
    `Bloqueado em: ${date.toLocaleString('pt-BR')}`;
document.getElementById('time').textContent = date.toLocaleTimeString('pt-BR');

// TIMER DE 5 MINUTOS
let secondsRemaining = 300;  // 5 minutos
const timerDisplay = document.getElementById('timer');

function updateTimer() {
    const minutes = Math.floor(secondsRemaining / 60);
    const seconds = secondsRemaining % 60;
    timerDisplay.textContent = `${minutes}:${seconds.toString().padStart(2, '0')}`;

    // Mudar cor conforme se aproxima do final
    if (secondsRemaining <= 30) {
        timerDisplay.style.color = '#ff6b6b';
        timerDisplay.style.textShadow = '0 0 20px #ff6b6b';
    } else if (secondsRemaining <= 60) {
        timerDisplay.style.color = '#ff9800';
    }

    if (secondsRemaining <= 0) {
        clearInterval(timerInterval);
        // Voltar para página anterior ou fechar aba
        window.history.back();
        setTimeout(() => {
            window.close();  // Se history.back() não funcionar, fechar aba
        }, 1000);
    }

    secondsRemaining--;
}

const timerInterval = setInterval(updateTimer, 1000);
updateTimer();  // Chamar uma vez imediatamente

// Log de atividade
const log = document.getElementById('log');
function addLog(message) {
    const entry = document.createElement('div');
    entry.className = 'log-entry';
    entry.textContent = `${new Date().toLocaleTimeString('pt-BR')} - ${message}`;
    log.appendChild(entry);
    log.scrollTop = log.scrollHeight;  // Scroll automático
}

// Tentar voltar = registrar
window.onpopstate = () => {
    addLog('Tentativa de voltar detectada');
};

// Tentar fechar a aba = registrar e bloquear
window.onbeforeunload = () => {
    addLog('Tentativa de fechar a aba');
    // Não deixar sair
    return 'Você não pode fechar esta página agora.';
};

// Impedir teclas de navegação
document.addEventListener('keydown', (e) => {
    // Alt+Left (voltar)
    if (e.altKey && e.key === 'ArrowLeft') {
        e.preventDefault();
        addLog('Tecla Alt+← bloqueada');
    }
    // Ctrl+W (fechar aba)
    if (e.ctrlKey && e.key === 'w') {
        e.preventDefault();
        addLog('Atalho Ctrl+W bloqueado');
    }
    // F5 (recarregar)
    if (e.key === 'F5') {
        e.preventDefault();
        addLog('Recarregar (F5) bloqueado');
    }
    // Ctrl+R (recarregar)
    if (e.ctrlKey && e.key === 'r') {
        e.preventDefault();
        addLog('Atalho Ctrl+R bloqueado');
    }
});

// Impedir clicar em links
document.addEventListener('click', (e) => {
    if (e.target.tagName === 'A') {
        e.preventDefault();
        addLog(`Tentativa de clicar em link bloqueada`);
    }
});

// Notificar app que a página foi mostrada
if (chrome?.runtime?.sendMessage) {
    chrome.runtime.sendMessage({
        type: 'CTC_BLOCKED_PAGE_SHOWN',
        reason: reason,
        timestamp: new Date().toISOString()
    }).catch(() => {
        // App pode estar desligado
    });
}

console.log('[CTC] Página de bloqueio ativada:', reason);

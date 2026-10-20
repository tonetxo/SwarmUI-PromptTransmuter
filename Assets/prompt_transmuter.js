"use strict";

class PromptTransmuterTool {
    constructor() {
        this.mainDiv = null;
    }

    register() {
        this.mainDiv = registerNewTool('prompt_transmuter', 'Prompt Transmuter');
        this.mainDiv.innerHTML = `
            <div class="pt-tool">
                <div class="pt-description">Mutate the current prompt into variants, then send one back or copy them.</div>
                <div class="pt-control-row">
                    <label class="pt-label">Mode
                        <select id="pt_mode" class="pt-select">
                            <option value="words">Words (cinematic vocabulary)</option>
                            <option value="internal">Internal (reuse prompt words)</option>
                            <option value="synonyms">Synonyms</option>
                        </select>
                    </label>
                    <label class="pt-label">Strength (%)
                        <input id="pt_strength" class="pt-number" type="number" min="0" max="100" step="1" value="10">
                    </label>
                    <label class="pt-label">Variants
                        <input id="pt_count" class="pt-number" type="number" min="1" max="16" step="1" value="4">
                    </label>
                    <label class="pt-label">Seed
                        <input id="pt_seed" class="pt-number" type="number" step="1" value="42">
                    </label>
                </div>
                <div class="pt-actions">
                    <button id="pt_transmute" class="basic-button">Transmute</button>
                    <button id="pt_copy" class="basic-button">Copy</button>
                    <button id="pt_send_first" class="basic-button">Send first variant to prompt</button>
                </div>
                <textarea id="pt_output" class="auto-text pt-output" rows="12" readonly placeholder="Variants will appear here…"></textarea>
                <div id="pt_status" class="pt-status"></div>
            </div>
        `;

        let output = document.getElementById('pt_output');
        let mode = document.getElementById('pt_mode');
        let strength = document.getElementById('pt_strength');
        let count = document.getElementById('pt_count');
        let seed = document.getElementById('pt_seed');
        let status = document.getElementById('pt_status');
        let promptBox = document.getElementById('alt_prompt_textbox');

        let setStatus = (msg, isError = false) => {
            status.textContent = msg;
            status.className = 'pt-status' + (isError ? ' pt-status-error' : '');
        };

        let getPrompt = () => promptBox ? promptBox.value : '';

        let setPrompt = (text) => {
            if (!promptBox) {
                setStatus('Prompt box not found', true);
                return false;
            }
            promptBox.value = text;
            triggerChangeFor(promptBox);
            return true;
        };

        document.getElementById('pt_transmute').addEventListener('click', () => {
            let prompt = getPrompt();
            if (!prompt.trim()) {
                setStatus('Prompt is empty', true);
                return;
            }
            setStatus('Transmuting…');
            genericRequest('TransmutePrompt', {
                prompt: prompt,
                mode: mode.value,
                strength: parseInt(strength.value, 10) || 0,
                count: parseInt(count.value, 10) || 4,
                seed: parseInt(seed.value, 10) || 0
            }, (data) => {
                if (!data.success) {
                    setStatus(data.error || 'Transmutation failed', true);
                    return;
                }
                output.value = data.text;
                let lines = data.text.split('\n').filter(l => l.trim() && !l.startsWith('---'));
                let msg = `${lines.length} variant(s) generated`;
                if (data.matched_words && data.matched_words.length) {
                    msg += ` · matched words: ${data.matched_words.join(', ')}`;
                }
                if (typeof data.replaced_count === 'number') {
                    msg += ` · ${data.replaced_count} total replacements`;
                }
                setStatus(msg);
            });
        });

        document.getElementById('pt_send_first').addEventListener('click', () => {
            let text = output.value.trim();
            if (!text) {
                setStatus('No variants to send', true);
                return;
            }
            let parts = text.split(/--- Variant \d+ ---/).map(s => s.trim()).filter(Boolean);
            let first = parts[0] || text;
            if (setPrompt(first)) {
                setStatus('Sent first variant to prompt.');
            }
        });

        document.getElementById('pt_copy').addEventListener('click', () => {
            let text = output.value;
            if (!text) {
                setStatus('No variants to copy', true);
                return;
            }
            navigator.clipboard.writeText(text).then(() => {
                setStatus('Copied to clipboard.');
            }).catch(() => {
                setStatus('Copy failed', true);
            });
        });

        this.mainDiv.addEventListener('tool-opened', () => {
            if (promptBox) {
                output.style.height = 'auto';
                textBoxSizeAdjust(output);
            }
        });
    }
}

sessionReadyCallbacks.push(() => {
    new PromptTransmuterTool().register();
});

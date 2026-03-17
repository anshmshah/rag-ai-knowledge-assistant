function app() {

    return {

        darkMode: false,
        showIntro: true,
        authLoading: false,
        mode: "rag",
        question: "",
        authenticated: false,
        authView: 'login',
        auth: { email: '', password: '' },
        sessions: [],
        selectedSessionId: "",
        selectedSessionTitle: "",
        messages: [],
        documents: [],
        selectedDoc: "",
        thinking: false,
        uploading: false,
        uploadProgress: 0,
        uploadMessage: "",
        ragHistory: [],
        generalHistory: [],
        ingestionJobs: [],
        currentEventSource: null,

        async init() {

            marked.setOptions({
                gfm: true,
                breaks: true
            });

            this.messages = []
            this.generalHistory = []
            this.ragHistory = []

            let theme = localStorage.getItem("theme")

            if (theme === "dark") {
                this.darkMode = true
            }

            // determine auth status
            const token = localStorage.getItem('token');
            if (token) {
                this.authenticated = true;
                // clear any stale client state then load server state
                this.clearClientState();
                await this.loadSessions();
                await this.loadDocuments();
            } else {
                this.authenticated = false;
            }

            // intro animation hide after load
            setTimeout(() => {
                this.showIntro = false
            }, 1200)



        },

        clearClientState() {
            this.sessions = [];
            this.messages = [];
            this.documents = [];
            this.selectedSessionId = '';
            this.selectedSessionTitle = '';
            this.selectedDoc = '';
            this.ragHistory = [];
            this.generalHistory = [];
        },

        async apiFetch(url, options = {}) {
            try {
                options = options || {};
                options.headers = options.headers || {};

                const token = localStorage.getItem('token');
                if (token) {
                    options.headers['Authorization'] = 'Bearer ' + token;
                }

                return await fetch(url, options);
            }
            catch (e) {
                console.error('apiFetch error', e);
                throw e;
            }
        },

        async login() {
            const email = this.auth.email?.trim();
            const password = this.auth.password || '';
            if (!email || !password) {
                alert('Email and password are required');
                return;
            }

            try {
                const res = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ email, password })
                });

                if (!res.ok) {
                    const txt = await res.text();
                    alert('Login failed: ' + txt);
                    return;
                }

                const body = await res.json();
                if (body.token) {
                    localStorage.setItem('token', body.token);
                    this.authLoading = false
                    // clear any previous client state
                    this.clearClientState();
                    this.authenticated = true;
                    this.auth.password = '';
                    await this.loadSessions();
                    await this.loadDocuments();
                }
            }
            catch (e) {
                console.error('Login error', e);
                alert('Login failed');
                this.authLoading = false
            }
        },

        async register() {
            const email = this.auth.email?.trim();
            const password = this.auth.password || '';
            if (!email || !password) {
                alert('Email and password are required');
                return;
            }

            this.authLoading = true
            try {
                const res = await fetch('/api/auth/register', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ email, password })
                });

                if (!res.ok) {
                    const txt = await res.text();
                    alert('Register failed: ' + txt);
                    return;
                }

                // auto login after successful registration
                await this.login();
                this.authLoading = false
            }
            catch (e) {
                console.error('Register error', e);
                alert('Registration failed');
                this.authLoading = false
            }
        },

        logout() {
            localStorage.removeItem('token');
            this.authenticated = false;
            // clear client state on logout to avoid leakage
            this.clearClientState();
            this.auth.email = '';
            this.auth.password = '';
            this.authView = 'login';
        },

        async loadSessions() {
            try {
                const res = await this.apiFetch('/api/sessions');
                console.log(res);
                if (!res.ok) return;
                const data = await res.json();
                this.sessions = data || [];

                // auto-select first session if none selected
                if (!this.selectedSessionId && this.sessions.length > 0) {
                    this.selectSession(this.sessions[0]);
                }
            }
            catch (e) {
                console.error('Failed to load sessions', e);
            }
        },

        async loadDocuments() {
            try {
                const res = await this.apiFetch('/api/documents');
                if (!res.ok) return;
                const list = await res.json();
                // map to simple filename array for existing UI
                this.documents = (list || []).map(d => d.fileName);
            }
            catch (e) {
                console.error('Failed to load documents', e);
            }
        },

        async createSession() {
            const title = prompt('Session title (optional)') || 'Chat';

            try {
                const res = await this.apiFetch('/api/sessions', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ title })
                });

                if (!res.ok) {
                    alert('Failed to create session');
                    return;
                }

                const created = await res.json();
                const session = { id: created.id, title: created.title, createdAt: created.createdAt, expiresAt: created.expiresAt };
                this.sessions.unshift(session);
                // clear previous client state before selecting new session
                this.messages = [];
                this.generalHistory = [];
                this.ragHistory = [];
                this.selectSession(session);
            }
            catch (e) {
                console.error('Create session failed', e);
                alert('Failed to create session');
            }
        },

        async selectSession(s) {
            if (!s) return;
            this.selectedSessionId = s.id;
            this.selectedSessionTitle = s.title || 'Chat';

            try {
                const res = await this.apiFetch(`/api/sessions/${s.id}`);
                if (!res.ok) {
                    console.error('Failed to load session messages');
                    return;
                }

                const body = await res.json();
                // body.messages is an array with {id, role, content, createdAt}
                this.messages = body.messages.map(m => ({ role: m.role, content: m.content, timestamp: m.createdAt }));
                this.scrollBottom();
            }
            catch (e) {
                console.error('Error loading session', e);
            }
        },

        async renameSession(s) {
            const title = prompt('New title for session', s.title || 'Chat');
            if (title === null) return; // cancelled

            try {
                const res = await this.apiFetch(`/api/sessions/${s.id}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ title })
                });

                if (!res.ok) {
                    alert('Failed to rename session');
                    return;
                }

                // update UI
                s.title = title;
                this.sessions = [...this.sessions];
            }
            catch (e) {
                console.error('renameSession error', e);
                alert('Failed to rename session');
            }
        },

        async deleteSession(s) {
            if (!confirm('Delete this session and its messages?')) return;

            try {
                const res = await this.apiFetch(`/api/sessions/${s.id}`, { method: 'DELETE' });
                if (!res.ok) {
                    alert('Failed to delete session');
                    return;
                }

                // remove from UI
                this.sessions = this.sessions.filter(x => x.id !== s.id);
                if (this.selectedSessionId === s.id) {
                    this.selectedSessionId = '';
                    this.messages = [];
                }
            }
            catch (e) {
                console.error('deleteSession error', e);
                alert('Failed to delete session');
            }
        },

        toggleTheme() {

            this.darkMode = !this.darkMode

            localStorage.setItem(
                "theme",
                this.darkMode ? "dark" : "light"
            )

        },

        toggleSidebar() {
            // placeholder for potential sidebar collapse in future
        },

        copyMessage(msg) {
            try {
                const text = msg.content || '';
                navigator.clipboard.writeText(text);
            } catch (e) { console.error(e) }
        },


        renderMessage(text) {
            // defensive: treat null/undefined and literal 'undefined' as empty
            if (text === null || text === undefined) return "";
            if (String(text).trim() === "undefined") return "";
            if (!text) return ""
            // Work on a copy
            let t = String(text);

            // Normalize Windows CRLF
            t = t.replace(/\r\n/g, "\n");

            // Ensure blank line before and after headings (##, ###, etc.)
            t = t.replace(/(^|\n)(#{2,6} .*?)\n*/g, (m, p1, p2) => `${p1}${p2}\n\n`);

            // Ensure lists have a blank line before and after block
            t = t.replace(/\n(\s*-\s+)/g, (m, p1) => `\n\n${p1}`);
            t = t.replace(/(\n\s*\n)(- .*?)(\n\s*\n|$)/gs, (m, p1, p2, p3) => `\n${p2}${p3}`);

            // Collapse multiple blank lines to max two
            t = t.replace(/\n{3,}/g, "\n\n");

            // Trim leading/trailing whitespace
            t = t.trim();

            return marked.parse(t);
        },

        stopStreaming() {

            if (this.currentEventSource) {
                this.currentEventSource.close()
                this.currentEventSource = null
            }

            this.thinking = false

        },

        async deleteDocument(doc) {

            if (!confirm("Delete this document?")) return

            try {

                const res = await this.apiFetch(`/api/AITest/document?name=${encodeURIComponent(doc)}`, {
                    method: "DELETE"
                })

                if (res.ok) {
                    await this.loadDocuments();
                    if (this.selectedDoc === doc) this.selectedDoc = "";
                } else {
                    alert('Failed to delete document');
                }

            }
            catch (err) {

                console.error(err)
                alert("Failed to delete document")

            }

        },

        clearChat() {

            this.messages = []
            this.generalHistory = []
            this.ragHistory = []

        },

        // ================================
        // SEND MESSAGE (STREAMING)
        // ================================


        async sendMessage() {

            if (!this.question) return

            let q = this.question

            const userMsg = {
                role: "user",
                content: q
            }

            // show in UI
            this.messages.push(userMsg)

            // save history depending on mode
            if (this.mode === "rag") {
                this.ragHistory.push(userMsg)
            } else {
                this.generalHistory.push(userMsg)
            }

            this.question = ""

            let aiMessage = {
                role: "assistant",
                content: "",
                sources: []
            }

            this.messages.push(aiMessage)

            try {

                // ========================
                // RAG MODE
                // ========================

                if (this.mode === "rag") {

                    let url = `/api/AITest/ask-rag-stream?question=${encodeURIComponent(q)}`

                    if (this.selectedDoc) {
                        url += `&doc=${encodeURIComponent(this.selectedDoc)}`
                    }

                    // include session id if selected
                    if (this.selectedSessionId) {
                        url += `&sessionId=${encodeURIComponent(this.selectedSessionId)}`
                    }

                    // include access token for SSE if present in localStorage
                    const token = localStorage.getItem('token');
                    if (token) {
                        url += `${url.includes('?') ? '&' : '?'}access_token=${encodeURIComponent(token)}`;
                    }

                    this.currentEventSource = new EventSource(url)
                    const eventSource = this.currentEventSource
                    //const eventSource = new EventSource(url)

                    const lastIndex = this.messages.length - 1

                    this.thinking = true

                    // Buffered streaming to preserve markdown and reduce re-renders
                    let buffer = ""
                    let flushTimer = null

                    const flushBuffer = () => {
                        if (!buffer) return

                        // Safe join: ensure space between existing content and buffer when needed
                        const curr = this.messages[lastIndex].content || "";
                        if (curr.length > 0) {
                            const lastChar = curr[curr.length - 1];
                            const firstChar = buffer[0];
                            if (!/\s/.test(lastChar) && firstChar && !/\s/.test(firstChar) && !/^\n/.test(buffer)) {
                                this.messages[lastIndex].content += " " + buffer;
                            } else {
                                this.messages[lastIndex].content += buffer;
                            }
                        } else {
                            this.messages[lastIndex].content += buffer;
                        }

                        buffer = ""
                        this.messages = [...this.messages]
                        this.scrollBottom()
                    }

                    eventSource.onmessage = (event) => {
                        // console.log("STREAM:", event.data)

                        if (event.data === "[DONE]") {
                            // flush remaining buffer and finalize
                            if (flushTimer) clearTimeout(flushTimer)
                            flushBuffer()
                            this.thinking = false
                            eventSource.close()

                            // Save in RAG history
                            this.ragHistory.push({
                                role: "assistant",
                                content: this.messages[lastIndex].content,
                                timestamp: Date.now(),
                                sources: this.messages[lastIndex].sources || []
                            })

                            return
                        }

                        // FINAL cleaned payload arrives prefixed with [FINAL]\n<content>
                        if (event.data.startsWith("[FINAL]")) {
                            // flush any buffer
                            if (flushTimer) clearTimeout(flushTimer)
                            flushBuffer()

                            // Extract final content (may include newlines)
                            const final = event.data.replace(/^\[FINAL\]\n?/, "");

                            // Replace message content entirely with final cleaned text
                            this.messages[lastIndex].content = final

                            // Refresh UI and save history
                            this.messages = [...this.messages]
                            this.ragHistory.push({ role: "assistant", content: final, timestamp: Date.now() })

                            return
                        }

                        // Accumulate incoming fragments
                        const token = event.data || "";

                        // If token starts with newline, keep as-is. Otherwise ensure token spacing
                        if (!token) return;

                        // ensure messages[lastIndex].content is a string
                        if (this.messages[lastIndex].content == null) this.messages[lastIndex].content = "";

                        if (buffer.length === 0 && this.messages[lastIndex].content && this.messages[lastIndex].content.length > 0) {
                            const lastChar = this.messages[lastIndex].content.slice(-1);
                            const firstChar = token[0];
                            if (!/\s/.test(lastChar) && !/\s/.test(firstChar) && !/^\n/.test(token)) {
                                buffer += " " + token;
                            } else {
                                buffer += token;
                            }
                        } else {
                            buffer += token;
                        }

                        // Debounce flush to update UI smoothly
                        if (flushTimer) clearTimeout(flushTimer)
                        flushTimer = setTimeout(flushBuffer, 80)
                    }

                    eventSource.onerror = () => {
                        if (flushTimer) clearTimeout(flushTimer)
                        flushBuffer()
                        eventSource.close()
                    }

                }

                // ========================
                // GENERAL CHAT
                // ========================

                else {

                    const response = await this.apiFetch("/api/AITest/chat", {
                        method: "POST",
                        headers: {
                            "Content-Type": "application/json"
                        },
                        body: JSON.stringify({
                            question: q,
                            history: this.generalHistory
                        })
                    })

                    const data = await response.json()

                    const lastIndex = this.messages.length - 1

                    const answer =
                        data.answer || data.response || "No response"

                    this.messages[lastIndex].content = answer

                    

                    // Save assistant response
                    this.generalHistory.push({
                        role: "assistant",
                        content: this.messages[lastIndex].content
                    })

                    // if a session is selected, optionally persist via backend (server persists on ask endpoints)

                    // Refresh UI
                    this.messages = [...this.messages]
                }

            }
            catch (err) {

                console.error(err)

                const lastIndex = this.messages.length - 1
                this.messages[lastIndex].content = "Error contacting AI service."

                this.thinking = false

            }

            this.scrollBottom()

        },

        

        // ================================
        // FORMAT AI RESPONSE
        // ================================

        formatMessage(text) {

            if (!text) return ""

            let lines = text.split("\n")
            let html = ""
            let inList = false

            for (let line of lines) {

                line = line.trim()

                // H3 headers
                if (line.startsWith("### ")) {
                    html += `<h3 class="font-semibold mt-3 mb-1">${line.replace("### ", "")}</h3>`
                }

                // H2 headers
                else if (line.startsWith("## ")) {
                    html += `<h2 class="font-semibold mt-3 mb-1">${line.replace("## ", "")}</h2>`
                }

                // Bullet points
                else if (line.startsWith("- ")) {

                    if (!inList) {
                        html += `<ul class="list-disc ml-5">`
                        inList = true
                    }

                    html += `<li>${line.replace("- ", "")}</li>`
                }

                else {

                    if (inList) {
                        html += `</ul>`
                        inList = false
                    }

                    html += `<p>${line}</p>`
                }
            }

            if (inList) {
                html += `</ul>`
            }

            return html
        },

        // ================================
        // SCROLL CHAT
        // ================================

        scrollBottom() {

            setTimeout(() => {

                const chat = document.getElementById("chatContainer")

                if (chat) {
                    chat.scrollTop = chat.scrollHeight
                }

            }, 100)

        },

        // ================================
        // FILE UPLOAD
        // ================================

        async uploadFile(e) {

            const file = e.target.files[0]

            if (!file) return

            const formData = new FormData()
            formData.append("file", file)

            this.uploading = true
            this.uploadProgress = 20
            this.uploadMessage = "Uploading file..."

            try {

                const res = await this.apiFetch("/api/AITest/upload", {
                    method: "POST",
                    body: formData
                })

                this.uploadProgress = 70
                this.uploadMessage = "Processing document..."

                const text = await res.text()

                this.uploadProgress = 100
                this.uploadMessage = "Document uploaded. Indexing in progress..."
                //this.uploadMessage = text


                // refresh server-side document list
                if (res.ok) await this.loadDocuments()

            }
            catch (err) {

                console.error(err)

                this.uploadMessage = "Upload failed."
                this.uploadProgress = 0

            }

            setTimeout(() => {
                this.uploading = false
            }, 2000)

        },

        // ================================
        // DRAG & DROP UPLOAD
        // ================================

        async handleDrop(e) {

            const file = e.dataTransfer.files[0]

            if (!file) return

            const formData = new FormData()
            formData.append("file", file)

            try {

                const res = await this.apiFetch("/api/AITest/upload", {
                    method: "POST",
                    body: formData
                })

                if (res.ok) {
                    await this.loadDocuments();
                }

            }
            catch (err) {

                console.error(err)
                alert("Upload failed")

            }

        },

        // ================================
        // SELECT DOCUMENT
        // ================================

        selectDocument(doc) {

            if (this.selectedDoc === doc) {

                this.selectedDoc = ""

            } else {

                this.selectedDoc = doc

            }

        }

    }

}


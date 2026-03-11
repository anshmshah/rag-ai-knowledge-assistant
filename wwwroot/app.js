function app() {

    return {

        darkMode: false,
        mode: "rag",
        question: "",
        messages: [],
        documents: [],
        selectedDoc: "",
        thinking: false,
        uploading: false,
        uploadProgress: 0,
        uploadMessage: "",
        ragHistory: [],
        generalHistory: [],

        init() {

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



        },

        toggleTheme() {

            this.darkMode = !this.darkMode

            localStorage.setItem(
                "theme",
                this.darkMode ? "dark" : "light"
            )

        },


        renderMessage(text) {
            if (!text) return ""
            return marked.parse(text.replace(/\n/g, "\n\n"))
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

                    const eventSource = new EventSource(url)

                    const lastIndex = this.messages.length - 1

                    this.thinking = true

                    eventSource.onmessage = (event) => {

                        console.log("STREAM:", event.data)

                        if (event.data === "[DONE]") {
                            this.thinking = false
                            eventSource.close()

                            
                            console.log(this.messages[lastIndex].content)
                            // Save in RAG history
                            this.ragHistory.push({
                                role: "assistant",
                                content: this.messages[lastIndex].content
                            })

                            // Force Alpine refresh
                            this.messages = [...this.messages]

                            return
                        }

                        // Append streamed token
                        this.messages[lastIndex].content += event.data 

                        // Refresh UI
                        this.messages = [...this.messages]

                        this.scrollBottom()
                    }

                    eventSource.onerror = () => {
                        eventSource.close()
                    }

                }

                // ========================
                // GENERAL CHAT
                // ========================

                else {

                    const response = await fetch("/api/AITest/chat", {
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

                    // Refresh UI
                    this.messages = [...this.messages]
                }

            }
            catch (err) {

                console.error(err)

                const lastIndex = this.messages.length - 1
                this.messages[lastIndex].content = "Error contacting AI service."

            }

            this.scrollBottom()

        },

        //async sendMessage() {

        //    if (!this.question) return

        //    let q = this.question

        //    this.messages.push({
        //        role: "user",
        //        content: q
        //    })

        //    this.question = ""

        //    let aiMessage = {
        //        role: "assistant",
        //        content: "",
        //        sources: []
        //    }

        //    this.messages.push(aiMessage)

        //    try {

        //        if (this.mode === "rag") {

        //            let url = `/api/AITest/ask-rag-stream?question=${encodeURIComponent(q)}`

        //            if (this.selectedDoc) {
        //                url += `&doc=${encodeURIComponent(this.selectedDoc)}`
        //            }

        //            //const eventSource = new EventSource(url)

        //            const eventSource = new EventSource(url)

        //            eventSource.onmessage = (event) => {

        //                console.log(event.data)

        //                if (event.data === "[DONE]") {
        //                    eventSource.close()
        //                    return
        //                }

        //                // update the last message directly
        //                const lastIndex = this.messages.length - 1
        //                this.messages[lastIndex].content += event.data + " "

        //                // apply formatting
        //                this.messages[lastIndex].content =
        //                    this.formatMessage(this.messages[lastIndex].content)

        //                this.scrollBottom()

        //            }

        //            eventSource.onerror = () => {
        //                eventSource.close()
        //            }

        //        }

        //        else {

        //            const response = await fetch("/api/AITest/chat", {
        //                method: "POST",
        //                headers: {
        //                    "Content-Type": "application/json"
        //                },
        //                body: JSON.stringify({
        //                    question: q,
        //                    history: this.messages
        //                })
        //            })
        //            const data = await response.json()

        //            const lastIndex = this.messages.length - 1

        //            this.messages[lastIndex].content =
        //                this.formatMessage(data.answer || data.response || "No response")


        //        }

        //    }
        //    catch (err) {

        //        console.error(err)

        //        aiMessage.content = "Error contacting AI service."

        //    }

        

        //    this.scrollBottom()

        //},

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

                const res = await fetch("/api/AITest/upload", {
                    method: "POST",
                    body: formData
                })

                this.uploadProgress = 70
                this.uploadMessage = "Processing document..."

                const text = await res.text()

                this.uploadProgress = 100
                this.uploadMessage = text

                if (!this.documents.includes(file.name)) {
                    this.documents.push(file.name)
                }

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

                await fetch("/api/AITest/upload", {
                    method: "POST",
                    body: formData
                })

                if (!this.documents.includes(file.name)) {
                    this.documents.push(file.name)
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

//function app() {

//    return {

//        darkMode: false,
//        mode: "rag",
//        question: "",
//        messages: [],
//        documents: [],
//        selectedDoc: "",
//        thinking: false,
//        uploading: false,
//        uploadProgress: 0,
//        uploadMessage: "",

//        init() {

//            let theme = localStorage.getItem("theme")

//            if (theme === "dark") {
//                this.darkMode = true
//            }

//        },

//        toggleTheme() {

//            this.darkMode = !this.darkMode

//            localStorage.setItem(
//                "theme",
//                this.darkMode ? "dark" : "light"
//            )

//        },

//        // ================================
//        // SEND MESSAGE
//        // ================================

//        async sendMessage() {

//            if (!this.question) return

//            let q = this.question

//            this.messages.push({
//                role: "user",
//                content: q
//            })

//            this.question = ""
//            this.thinking = true

//            try {

//                let url = ""

//                if (this.mode === "rag") {

//                    url = `/api/AITest/ask-rag?question=${encodeURIComponent(q)}`

//                    if (this.selectedDoc) {
//                        url += `&doc=${encodeURIComponent(this.selectedDoc)}`
//                    }

//                } else {

//                    url = `/api/AITest/chat?question=${encodeURIComponent(q)}`

//                }

//                const response = await fetch(url)

//                const data = await response.json()

//                this.messages.push({
//                    role: "assistant",
//                    content: this.formatMessage(data.answer || data.response || "No response"),
//                    sources: data.sources || []
//                })

//            }
//            catch (err) {

//                console.error(err)

//                this.messages.push({
//                    role: "assistant",
//                    content: "Error contacting AI service."
//                })

//            }

//            this.thinking = false

//            this.scrollBottom()

//        },

//        // ================================
//        // FORMAT AI RESPONSE
//        // ================================

//        formatMessage(text) {

//            if (!text) return ""

//            return text
//                .replace(/\n/g, "<br>")
//                .replace(/### (.*?)<br>/g, "<h3 class='font-semibold mt-3'>$1</h3>")
//                .replace(/- (.*?)(<br>|$)/g, "<li>$1</li>")

//        },

//        // ================================
//        // SCROLL CHAT
//        // ================================

//        scrollBottom() {

//            setTimeout(() => {

//                const chat = document.getElementById("chatContainer")

//                if (chat) {
//                    chat.scrollTop = chat.scrollHeight
//                }

//            }, 100)

//        },

//        // ================================
//        // FILE UPLOAD
//        // ================================

//        async uploadFile(e) {

//            const file = e.target.files[0]

//            if (!file) return

//            const formData = new FormData()
//            formData.append("file", file)

//            this.uploading = true
//            this.uploadProgress = 20
//            this.uploadMessage = "Uploading file..."

//            try {

//                const res = await fetch("/api/AITest/upload", {
//                    method: "POST",
//                    body: formData
//                })

//                this.uploadProgress = 70
//                this.uploadMessage = "Processing document..."

//                const text = await res.text()

//                this.uploadProgress = 100
//                this.uploadMessage = text

//                if (!this.documents.includes(file.name)) {
//                    this.documents.push(file.name)
//                }

//            }
//            catch (err) {

//                console.error(err)

//                this.uploadMessage = "Upload failed."
//                this.uploadProgress = 0

//            }

//            setTimeout(() => {
//                this.uploading = false
//            }, 2000)

//        },

//        // ================================
//        // DRAG & DROP UPLOAD
//        // ================================

//        async handleDrop(e) {

//            const file = e.dataTransfer.files[0]

//            if (!file) return

//            const formData = new FormData()
//            formData.append("file", file)

//            try {

//                await fetch("/api/AITest/upload", {
//                    method: "POST",
//                    body: formData
//                })

//                if (!this.documents.includes(file.name)) {
//                    this.documents.push(file.name)
//                }

//            }
//            catch (err) {

//                console.error(err)
//                alert("Upload failed")

//            }

//        },

//        // ================================
//        // SELECT DOCUMENT
//        // ================================

//        selectDocument(doc) {

//            if (this.selectedDoc === doc) {

//                this.selectedDoc = ""

//            } else {

//                this.selectedDoc = doc

//            }

//        }

//    }

//}


//old workin code

//function app() {

//    return {

//        darkMode: false,
//        mode: "rag",
//        question: "",
//        messages: [],
//        documents: [],
//        thinking: false,
//        uploading: false,
//        uploadProgress: 0,
//        uploadMessage: "",
//        selectedDoc: "",

//        init() {

//            // Load saved theme
//            let theme = localStorage.getItem("theme")

//            if (theme === "dark") {
//                this.darkMode = true
//            }

//        },

//        toggleTheme() {

//            this.darkMode = !this.darkMode

//            localStorage.setItem(
//                "theme",
//                this.darkMode ? "dark" : "light"
//            )

//        },

//        async sendMessage() {

//            if (!this.question) return

//            let q = this.question

//            // Add user message
//            this.messages.push({
//                role: "user",
//                content: q
//            })

//            this.question = ""

//            this.thinking = true

//            try {

//                let url = ""

//                if (this.mode === "rag") {
//                    url = `/api/AITest/ask-rag?question=${encodeURIComponent(q)}`
//                }
//                else {
//                    url = `/api/AITest/chat?question=${encodeURIComponent(q)}&mode=chat`
//                }

//                const response = await fetch(url)

//                const data = await response.json()

//                // Add AI response
//                this.messages.push({
//                    role: "assistant",
//                    content: data.answer || data.response || "No response",
//                    sources: data.sources || []
//                })

//            }
//            catch (err) {

//                console.error(err)

//                this.messages.push({
//                    role: "assistant",
//                    content: "Error contacting AI service."
//                })

//            }

//            this.thinking = false

//            this.scrollBottom()

//        },

//        scrollBottom() {

//            setTimeout(() => {

//                const chat = document.getElementById("chatContainer")

//                if (chat) {
//                    chat.scrollTop = chat.scrollHeight
//                }

//            }, 100)

//        },

//        async uploadFile(e) {

//            const file = e.target.files[0]

//            if (!file) return

//            const formData = new FormData()
//            formData.append("file", file)

//            this.uploading = true
//            this.uploadProgress = 20
//            this.uploadMessage = "Uploading file..."

//            try {

//                const res = await fetch("/api/AITest/upload", {
//                    method: "POST",
//                    body: formData
//                })

//                this.uploadProgress = 70
//                this.uploadMessage = "Processing document..."

//                const text = await res.text()

//                this.uploadProgress = 100
//                this.uploadMessage = text

//                this.documents.push(file.name)

//            }
//            catch (err) {

//                console.error(err)

//                this.uploadMessage = "Upload failed."
//                this.uploadProgress = 0

//            }

//            setTimeout(() => {
//                this.uploading = false
//            }, 2000)

//        },

//        async handleDrop(e) {

//            const file = e.dataTransfer.files[0]

//            if (!file) return

//            const formData = new FormData()

//            formData.append("file", file)

//            try {

//                await fetch("/api/AITest/upload", {
//                    method: "POST",
//                    body: formData
//                })

//                this.documents.push(file.name)

//            }
//            catch (err) {

//                console.error(err)

//                alert("Upload failed")

//            }

//        }

//    }

//}
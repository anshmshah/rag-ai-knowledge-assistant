import os
from fpdf import FPDF

pdf = FPDF()
pdf.add_page()
pdf.set_font("Arial", size=15)
pdf.cell(200, 10, txt="Welcome to LocalRagAPI Demo!", ln=1, align='C')
pdf.set_font("Arial", size=12)

content = """
What is RAG?
Retrieval-Augmented Generation (RAG) is a technique that grounds Large Language Models (LLMs) on your specific, private data. Instead of relying solely on the AI's pre-trained knowledge, RAG searches your uploaded documents for relevant information and provides it to the AI to answer your question accurately.

Why use RAG?
LLMs are prone to hallucinations (making things up) and they don't know your company secrets, personal notes, or proprietary code. RAG solves this by ensuring the AI bases its answers exclusively on the documents you upload.

How this Application Works:
1. Document Upload: When you upload a PDF, we extract its text.
2. Chunking: We break the text into smaller, overlapping chunks (sentences/paragraphs).
3. Embeddings: Each chunk is converted into a vector (a list of numbers representing the meaning of the text) using Jina Embeddings.
4. Vector Database: These vectors are stored in Qdrant, our vector database.
5. Similarity Search: When you ask a question, we convert your question into a vector and find the most similar chunks in Qdrant.
6. Answer Generation: We send the relevant chunks and your question to Groq (a fast LLM inference engine) to generate a final answer with citations.

Suggested Prompts to Try Now:
- What is RAG?
- Explain chunking.
- What are embeddings?
- How does this project work?
- What technologies power this application?

Ready? Upload your own documents to build your personal knowledge base!
"""

# add text with multi_cell
pdf.multi_cell(0, 10, txt=content)

pdf.output("DemoDocuments/Demo Guide.pdf")
print("PDF created successfully.")

FROM python:3.12-slim

# Install ffmpeg (yt-dlp uses it for some streams) and curl (for the healthcheck).
RUN apt-get update && apt-get install -y --no-install-recommends \
        ffmpeg \
        curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
# Always use the very latest yt-dlp at build time (YouTube breaks constantly).
RUN pip install --no-cache-dir -U --pre yt-dlp

COPY app.py .

EXPOSE 8077

# uvicorn with --workers 2 — small enough for a Pi, big enough to overlap requests.
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8077", "--workers", "2"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8077/health || exit 1

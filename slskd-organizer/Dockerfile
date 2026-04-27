FROM python:3.12-slim

# mutagen is the only runtime dep — small (~600KB), pure Python.
RUN pip install --no-cache-dir mutagen==1.47.0

WORKDIR /app
COPY app.py /app/app.py

# Run unbuffered so docker logs surface output immediately rather than
# waiting for the Python stdout buffer to flush.
ENV PYTHONUNBUFFERED=1

CMD ["python", "/app/app.py"]

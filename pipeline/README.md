# pipeline (TransNetV2 sidecar)

The one part of the pipeline that stays in Python: TransNetV2 shot-boundary
detection (PyTorch). The .NET worker (`pipeline-dotnet`) launches
`python3 -m highlighter_pipeline.shots_sidecar` from this directory and talks
to it over JSON lines — one `{"path", "start_seconds"}` request per video
segment, one `{"cuts": [...]}` reply. If Python or the model dependencies are
missing, the worker logs once and runs without scene cuts.

```bash
pip install transnetv2-pytorch numpy   # or: pip install ./pipeline
```

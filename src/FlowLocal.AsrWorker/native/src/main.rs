mod identity;

use base64::Engine;
use identity::Verifier;
use parakeet_rs::MultitalkerASR;
use serde_json::{Value, json};
use std::io::{self, BufRead, Write};
use std::path::PathBuf;
use std::time::Instant;

fn emit(value: Value) {
    println!("{value}");
    let _ = io::stdout().flush();
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let root = PathBuf::from(std::env::var("FLOWLOCAL_MODELS_DIR")?);
    let identity_path = root.parent().ok_or("Missing app data directory")?.join("user-embedding.json");
    let mut model = MultitalkerASR::from_pretrained(&root, root.join("diar_streaming_sortformer_4spk-v2.1.onnx"), None)?;
    let mut verifier = Verifier::new(&root.join("ecapa-speaker-v1.onnx"), &root.join("fbank-80x201-f32.bin"), &identity_path)?;
    emit(json!({"evt":"status","state":"Ready","detail":"Sortformer v2.1 + Multitalker INT8 CPU"}));
    let mut active = false;
    let mut enrollment = false;
    let mut buffer: Vec<f32> = Vec::new();
    let mut text = String::new();
    let mut samples = 0usize;
    let mut processing = 0.0;
    let mut partial_count = 0usize;
    let mut first_partial = None;
    for line in io::stdin().lock().lines() {
        let line = line?;
        let outcome = (|| -> Result<(), Box<dyn std::error::Error>> {
            let request: Value = serde_json::from_str(&line)?;
            let command = request["cmd"].as_str().ok_or("Missing command")?;
            match command {
                "init" => emit(json!({"evt":"ok","enrolled":verifier.enrolled()})),
                "start" | "enroll" => {
                    model.reset(); verifier.reset(); buffer.clear(); text.clear(); samples = 0; processing = 0.0; partial_count = 0; first_partial = None;
                    enrollment = command == "enroll";
                    if !enrollment && !verifier.enrolled() { return Err("Enroll your voice before dictation".into()); }
                    active = true;
                    emit(json!({"evt":"ok"}));
                }
                "push" => {
                    if !active { return Err("No active recording".into()); }
                    let pcm = base64::engine::general_purpose::STANDARD.decode(request["b64"].as_str().ok_or("Missing audio")?)?;
                    if pcm.len() % 2 != 0 { return Err("Invalid PCM16 data".into()); }
                    let incoming = pcm.chunks_exact(2).map(|bytes| i16::from_le_bytes([bytes[0],bytes[1]]) as f32 / 32768.0);
                    samples += pcm.len()/2;
                    buffer.extend(incoming);
                    if !enrollment {
                        let chunk = model.chunk_audio_samples();
                        let mut consumed = 0;
                        while buffer.len() - consumed >= chunk {
                            let audio = &buffer[consumed..consumed + chunk];
                            consumed += chunk;
                            let began = Instant::now();
                            let mut identity_error = None;
                            model.transcribe_chunk_with_activity(audio, |audio, activity| {
                                if let Err(error) = verifier.observe(audio, activity) { identity_error = Some(error.to_string()); }
                            })?;
                            if let Some(error) = identity_error { return Err(error.into()); }
                            processing += began.elapsed().as_secs_f64()*1000.0;
                            if let Some(id) = verifier.user() {
                                let updated = model.transcript_since(id, verifier.user_from());
                                if updated != text {
                                    text = updated;
                                    partial_count += 1;
                                    first_partial.get_or_insert(samples as f64 / 16.0);
                                    emit(json!({"evt":"partial","text":text,"audioMs":samples as f64 / 16.0}));
                                }
                            }
                        }
                        buffer.drain(..consumed);
                    }
                }
                "complete" => {
                    if !active || enrollment { return Err("No active dictation".into()); }
                    let began = Instant::now();
                    if !buffer.is_empty() {
                        let chunk = model.chunk_audio_samples();
                        buffer.resize(chunk,0.0);
                        let audio = std::mem::take(&mut buffer);
                        let mut identity_error = None;
                        model.transcribe_chunk_with_activity(&audio, |audio, activity| {
                            if let Err(error) = verifier.observe(audio, activity) { identity_error = Some(error.to_string()); }
                        })?;
                        if let Some(error) = identity_error { return Err(error.into()); }
                    }
                    let flush = vec![0.0f32; model.chunk_audio_samples()];
                    for _ in 0..3 { model.transcribe_chunk_with_activity(&flush, |_,_| {})?; }
                    let final_text = verifier.user().map(|id| model.transcript_since(id, verifier.user_from())).unwrap_or_default();
                    active = false;
                    emit(json!({"evt":"final","text":final_text,"metrics":{"finalizeMs":began.elapsed().as_secs_f64()*1000.0,"processingMs":processing,"audioMs":samples as f64/16.0,"partialCount":partial_count,"firstPartialAudioMs":first_partial}}));
                }
                "finish_enroll" => {
                    if !active || !enrollment { return Err("No active enrollment".into()); }
                    verifier.enroll(&buffer, &identity_path)?;
                    buffer.clear(); active = false; enrollment = false;
                    emit(json!({"evt":"ok","enrolled":true}));
                }
                "cancel" => { active = false; enrollment = false; buffer.clear(); model.reset(); verifier.reset(); emit(json!({"evt":"ok"})); }
                "exit" => return Ok(()),
                _ => return Err("Unknown worker command".into()),
            }
            Ok(())
        })();
        if let Err(error) = outcome { active = false; enrollment = false; buffer.clear(); model.reset(); verifier.reset(); emit(json!({"evt":"error","message":error.to_string()})); }
        if serde_json::from_str::<Value>(&line)?["cmd"] == "exit" { break; }
    }
    Ok(())
}

fn main() {
    if let Err(error) = run() { emit(json!({"evt":"error","message":error.to_string()})); std::process::exit(1); }
}

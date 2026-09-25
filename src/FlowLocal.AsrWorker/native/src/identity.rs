use ndarray::{Array1, Array3};
use ort::session::Session;
use ort::value::TensorRef;
use realfft::RealFftPlanner;
use std::fs;
use std::path::Path;

const RATE: usize = 16_000;
const FFT: usize = 400;
const HOP: usize = 160;
const BINS: usize = 80;
const DIM: usize = 192;
// SpeechBrain SpeakerRecognition.verify_batch uses cosine > 0.25.
const MATCH_THRESHOLD: f32 = 0.25;

pub struct Verifier {
    model: Session,
    filters: Vec<f32>,
    reference: Option<Vec<f32>>,
    clean: [Vec<f32>; 4],
    scores: [Option<f32>; 4],
    clean_since: [usize; 4],
    score_since: [usize; 4],
    rejected: [bool; 4],
    last_clean_end: [usize; 4],
    samples_seen: usize,
    user_from: f32,
    user: Option<usize>,
}

impl Verifier {
    pub fn new(model_path: &Path, filters_path: &Path, reference_path: &Path) -> Result<Self, Box<dyn std::error::Error>> {
        let bytes = fs::read(filters_path)?;
        if bytes.len() != BINS * (FFT / 2 + 1) * 4 { return Err("Invalid speaker filterbank".into()); }
        let filters = bytes.chunks_exact(4).map(|v| f32::from_le_bytes(v.try_into().unwrap())).collect();
        let reference = if reference_path.exists() {
            let value: Vec<f32> = serde_json::from_slice(&fs::read(reference_path)?)?;
            if value.len() != DIM || !value.iter().all(|v| v.is_finite()) { return Err("Invalid enrollment embedding".into()); }
            Some(value)
        } else { None };
        Ok(Self { model: Session::builder()?.with_intra_threads(2)?.commit_from_file(model_path)?, filters, reference, clean: std::array::from_fn(|_| Vec::new()), scores: [None; 4], clean_since: [0; 4], score_since: [0; 4], rejected: [false; 4], last_clean_end: [0; 4], samples_seen: 0, user_from: 0.0, user: None })
    }

    pub fn enrolled(&self) -> bool { self.reference.is_some() }
    pub fn user(&self) -> Option<usize> { self.user }
    pub fn user_from(&self) -> f32 { self.user_from }
    pub fn reset(&mut self) {
        self.clean.iter_mut().for_each(Vec::clear);
        self.scores = [None; 4];
        self.clean_since = [0; 4];
        self.score_since = [0; 4];
        self.rejected = [false; 4];
        self.last_clean_end = [0; 4];
        self.samples_seen = 0;
        self.user_from = 0.0;
        self.user = None;
    }
    pub fn enroll(&mut self, audio: &[f32], path: &Path) -> Result<(), Box<dyn std::error::Error>> {
        if audio.len() < RATE * 8 { return Err("Record at least eight seconds of clean speech".into()); }
        if audio.iter().map(|sample| sample * sample).sum::<f32>() / (audio.len() as f32) < 0.0001 {
            return Err("Enrollment is too quiet; speak clearly near the microphone".into());
        }
        let embedding = self.embed(audio)?;
        fs::create_dir_all(path.parent().ok_or("Missing enrollment directory")?)?;
        let temporary = path.with_extension("tmp");
        fs::write(&temporary, serde_json::to_vec(&embedding)?)?;
        fs::rename(&temporary, path)?;
        self.reference = Some(embedding);
        self.reset();
        Ok(())
    }
    pub fn observe(&mut self, audio: &[f32], activity: &ndarray::Array2<f32>) -> Result<(), Box<dyn std::error::Error>> {
        if self.user.is_some() || self.reference.is_none() { return Ok(()); }
        for frame in 0..activity.nrows() {
            let Some(speaker) = sole_speaker(activity, frame) else { continue };
            let start = frame * HOP;
            if start + HOP <= audio.len() {
                if self.clean[speaker].is_empty() { self.clean_since[speaker] = self.samples_seen + start; }
                self.clean[speaker].extend_from_slice(&audio[start..start + HOP]);
                self.last_clean_end[speaker] = self.samples_seen + start + HOP;
            }
        }
        self.samples_seen += audio.len();
        self.score_pending(RATE * 3)
    }

    pub fn finish(&mut self) -> Result<(), Box<dyn std::error::Error>> {
        self.score_pending(RATE * 4 / 5)
    }

    pub fn rejection(&self) -> &'static str {
        if self.scores.iter().all(Option::is_none) {
            "Insufficient clean speech to verify your voice."
        } else {
            "Speech was detected, but your enrolled voice could not be verified."
        }
    }

    fn score_pending(&mut self, minimum: usize) -> Result<(), Box<dyn std::error::Error>> {
        if self.user.is_some() || self.reference.is_none() { return Ok(()); }
        let mut scored = [false; 4];
        let mut tail_since = [None; 4];
        for spk in 0..4 {
            if self.clean[spk].len() < minimum { continue; }
            self.score_since[spk] = self.clean_since[spk];
            let segment = std::mem::take(&mut self.clean[spk]);
            let embedding = self.embed(&segment)?;
            let tail_len = segment.len().min(RATE * 2);
            let tail_embedding = if self.rejected[spk] { Some(self.embed(&segment[segment.len() - tail_len..])?) } else { None };
            let reference = self.reference.as_ref().unwrap();
            self.scores[spk] = Some(embedding.iter().zip(reference).map(|(a,b)| a*b).sum());
            if let Some(tail) = tail_embedding {
                let cosine: f32 = tail.iter().zip(reference).map(|(a,b)| a*b).sum();
                if std::env::var_os("FLOWLOCAL_DEBUG_IDENTITY").is_some() { eprintln!("speaker {spk}: tail cosine {cosine:.4}"); }
                if cosine > MATCH_THRESHOLD { tail_since[spk] = Some(self.last_clean_end[spk].saturating_sub(tail_len)); }
            }
            scored[spk] = true;
            if self.scores[spk].unwrap() <= MATCH_THRESHOLD {
                self.rejected[spk] = true;
            }
            if std::env::var_os("FLOWLOCAL_DEBUG_IDENTITY").is_some() {
                eprintln!("speaker {spk}: cosine {:.4}", self.scores[spk].unwrap());
            }
        }
        let mut ranked: Vec<_> = self.scores.iter().enumerate().filter_map(|(id, score)| score.map(|s| (id,s))).collect();
        ranked.sort_by(|a,b| b.1.total_cmp(&a.1));
        if let Some(&(id, score)) = ranked.first() {
            if scored[id] && score > MATCH_THRESHOLD && ranked.get(1).is_none_or(|(_, other)| score - other >= 0.12) {
                if !self.rejected[id] || tail_since[id].is_some() {
                    self.user = Some(id);
                    self.user_from = tail_since[id].unwrap_or(self.score_since[id]) as f32 / RATE as f32;
                }
            }
        }
        Ok(())
    }

    fn embed(&mut self, audio: &[f32]) -> Result<Vec<f32>, Box<dyn std::error::Error>> {
        if audio.len() < FFT { return Err("Insufficient speaker audio".into()); }
        let frames = audio.len() / HOP + 1;
        let mut features = Array3::<f32>::zeros((1, frames, BINS));
        let mut planner = RealFftPlanner::<f32>::new();
        let fft = planner.plan_fft_forward(FFT);
        let mut input = fft.make_input_vec();
        let mut spectrum = fft.make_output_vec();
        let mut scratch = fft.make_scratch_vec();
        let mut maximum = f32::NEG_INFINITY;
        for frame in 0..frames {
            for i in 0..FFT {
                let position = frame * HOP + i;
                let sample = position.checked_sub(FFT / 2).and_then(|index| audio.get(index)).copied().unwrap_or(0.0);
                input[i] = sample * (0.54 - 0.46 * (2.0 * std::f32::consts::PI * i as f32 / FFT as f32).cos());
            }
            fft.process_with_scratch(&mut input, &mut spectrum, &mut scratch)?;
            for mel in 0..BINS {
                let energy: f32 = spectrum.iter().enumerate().map(|(bin, value)| value.norm_sqr() * self.filters[bin * BINS + mel]).sum();
                let db = 10.0 * energy.max(1e-10).log10();
                features[[0, frame, mel]] = db;
                maximum = maximum.max(db);
            }
        }
        features.mapv_inplace(|value| value.max(maximum - 80.0));
        for mel in 0..BINS {
            let mean = (0..frames).map(|frame| features[[0, frame, mel]]).sum::<f32>() / frames as f32;
            for frame in 0..frames { features[[0, frame, mel]] -= mean; }
        }
        let lens = Array1::from_vec(vec![1.0f32]);
        let outputs = self.model.run(ort::inputs!["features" => TensorRef::from_array_view(features.view())?, "feature_lens" => TensorRef::from_array_view(lens.view())?])?;
        let (_, data) = outputs["embedding"].try_extract_tensor::<f32>()?;
        let norm = data.iter().map(|v| v*v).sum::<f32>().sqrt();
        if !norm.is_finite() || norm < 1e-8 { return Err("Speaker embedding has zero energy".into()); }
        Ok(data.iter().map(|v| v / norm).collect())
    }
}

fn sole_speaker(activity: &ndarray::Array2<f32>, frame: usize) -> Option<usize> {
    let mut candidate = None;
    for speaker in 0..4 {
        let confidence = activity[[frame, speaker]];
        if confidence >= 0.2 {
            if candidate.is_some() { return None; }
            if confidence < 0.75 { return None; }
            candidate = Some(speaker);
        }
    }
    candidate
}

#[cfg(test)]
mod tests {
    use super::sole_speaker;
    #[test]
    fn overlap_does_not_authorize_a_voice() {
        let activity = ndarray::arr2(&[[0.9, 0.8, 0.0, 0.0], [0.9, 0.1, 0.0, 0.0]]);
        assert_eq!(sole_speaker(&activity, 0), None);
        assert_eq!(sole_speaker(&activity, 1), Some(0));
    }
}

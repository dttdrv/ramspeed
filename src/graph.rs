/// Rolling history of memory usage for the graph.
pub struct UsageHistory {
    points: Vec<f64>,
    max_points: usize,
}

impl UsageHistory {
    pub fn new(max_points: usize) -> Self {
        Self {
            points: Vec::with_capacity(max_points),
            max_points,
        }
    }

    pub fn push(&mut self, usage_percent: f64) {
        self.points.push(usage_percent);
        if self.points.len() > self.max_points {
            self.points.remove(0);
        }
    }

    pub fn points(&self) -> &[f64] {
        &self.points
    }

    pub fn len(&self) -> usize {
        self.points.len()
    }
}

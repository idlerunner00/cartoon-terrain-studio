// Analytic area of periodic ink stripes. Work around the local period to avoid
// subtracting large world coordinates. The mean survives minification as tone.
float comic_line_integral(float x) {
    return floor(x) * .17 + clamp(fract(x) - .415, 0.0, .17);
}

float comic_line(float coordinate) {
    float footprint = max(fwidth(coordinate), .0001);
    float phase = fract(coordinate);
    float line = (comic_line_integral(phase + footprint * .5)
        - comic_line_integral(phase - footprint * .5)) / footprint;
    // Once a period falls below two pixels, retain the ink's average instead
    // of alternately resolving and losing lines as the camera moves.
    return mix(clamp(line, 0.0, 1.0), .17, smoothstep(.35, .85, footprint));
}

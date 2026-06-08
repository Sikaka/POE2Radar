namespace POE2Radar.Overlay;

/// <summary>
/// Solves the atlas canvas→screen homography (perspective transform) from node↔cursor correspondences.
/// The atlas is a tilted plane, so a plain affine can't be exact; a homography from 4+ spread points is.
/// Each point is <c>[relX, relY, screenX, screenY]</c>. Returns the 8 coefficients h0..h7 (h8=1):
///   w = h6·x + h7·y + 1;  sx = (h0·x + h1·y + h2)/w;  sy = (h3·x + h4·y + h5)/w
///
/// <para>The fit uses <b>Hartley normalization</b> (translate each point set's centroid to the origin and
/// scale so the mean distance is √2) before the DLT, then denormalizes. This is essential: the raw
/// coordinates run to thousands, so the perspective columns (−u·x) are ~10⁶ while the affine columns are
/// ~1 — forming the normal equations on unnormalized data squares that spread to ~10¹³ and the perspective
/// terms come out as roundoff noise. Normalized, the system is well-conditioned and the persp terms are
/// meaningful.</para>
/// </summary>
internal static class AtlasHomography
{
    /// <summary>Least-squares DLT fit over all points (needs 4+), with Hartley normalization.
    /// Returns the 8 coefficients h0..h7 (h8 normalized to 1). Null if the system is singular.</summary>
    public static double[]? Fit(System.Collections.Generic.List<double[]> ps)
    {
        if (ps.Count < 4) return null;

        // Normalization transforms for source (x,y) and dest (u,v): point' = s·(point − centroid).
        var (sxS, cxS, cyS) = NormParams(ps, 0);
        var (sxD, cxD, cyD) = NormParams(ps, 2);
        if (sxS == 0 || sxD == 0) return null;

        int n2 = ps.Count * 2;
        var A = new double[n2, 8]; var b = new double[n2];
        for (var i = 0; i < ps.Count; i++)
        {
            // Normalized coordinates.
            double x = sxS * (ps[i][0] - cxS), y = sxS * (ps[i][1] - cyS);
            double u = sxD * (ps[i][2] - cxD), v = sxD * (ps[i][3] - cyD);
            int r0 = i * 2;
            A[r0, 0] = x; A[r0, 1] = y; A[r0, 2] = 1; A[r0, 6] = -u * x; A[r0, 7] = -u * y; b[r0] = u;
            A[r0 + 1, 3] = x; A[r0 + 1, 4] = y; A[r0 + 1, 5] = 1; A[r0 + 1, 6] = -v * x; A[r0 + 1, 7] = -v * y; b[r0 + 1] = v;
        }
        // Normal equations N = AᵀA (8×8), rhs = Aᵀb. Well-conditioned now that coords are normalized.
        var N = new double[8, 8]; var rhs = new double[8];
        for (var r = 0; r < 8; r++)
        {
            for (var c = 0; c < 8; c++) { double s = 0; for (var k = 0; k < n2; k++) s += A[k, r] * A[k, c]; N[r, c] = s; }
            double sb = 0; for (var k = 0; k < n2; k++) sb += A[k, r] * b[k]; rhs[r] = sb;
        }
        var hn = SolveLinear(N, rhs, 8);
        if (hn == null) return null;

        // Hn (normalized homography, row-major 3×3, Hn[8]=1).
        var Hn = new[] { hn[0], hn[1], hn[2], hn[3], hn[4], hn[5], hn[6], hn[7], 1.0 };
        // Denormalize: H = Tdst⁻¹ · Hn · Tsrc, where T = [[s,0,−s·cx],[0,s,−s·cy],[0,0,1]].
        var Tsrc = new[] { sxS, 0, -sxS * cxS, 0, sxS, -sxS * cyS, 0, 0, 1.0 };
        var TdstInv = new[] { 1 / sxD, 0, cxD, 0, 1 / sxD, cyD, 0, 0, 1.0 };
        var H = Mul3(TdstInv, Mul3(Hn, Tsrc));
        if (System.Math.Abs(H[8]) < 1e-12) return null;
        for (var i = 0; i < 9; i++) H[i] /= H[8]; // normalize so h8 = 1
        return new[] { H[0], H[1], H[2], H[3], H[4], H[5], H[6], H[7] };
    }

    /// <summary>Least-squares <b>affine</b> fit (6-DOF, persp terms = 0) — the no-perspective baseline.
    /// Returned in the same 8-coeff layout (h6=h7=0) so it can be compared with <see cref="Fit"/>.</summary>
    public static double[]? FitAffine(System.Collections.Generic.List<double[]> ps)
    {
        if (ps.Count < 3) return null;
        // Solve [a b c] for u and [d e f] for v independently: u = a·x + b·y + c.
        var N = new double[3, 3]; var ru = new double[3]; var rv = new double[3];
        foreach (var p in ps)
        {
            double x = p[0], y = p[1], u = p[2], v = p[3];
            double[] row = { x, y, 1 };
            for (var r = 0; r < 3; r++) { for (var c = 0; c < 3; c++) N[r, c] += row[r] * row[c]; ru[r] += row[r] * u; rv[r] += row[r] * v; }
        }
        var au = SolveLinear(N, ru, 3); var av = SolveLinear((double[,])N.Clone(), rv, 3);
        if (au == null || av == null) return null;
        return new[] { au[0], au[1], au[2], av[0], av[1], av[2], 0.0, 0.0 };
    }

    /// <summary>Least-squares <b>scale + translation</b> fit (4-DOF: sx·x+tx, sy·y+ty — NO shear, NO
    /// perspective). This is the atlas's true model (screen = UIscale×zoom × relPos + canvas origin),
    /// validated live: the only honest degrees of freedom. Returned in the 8-coeff layout. Null if the
    /// points don't span in x or y.</summary>
    public static double[]? FitScaleTranslate(System.Collections.Generic.List<double[]> ps)
    {
        if (ps.Count < 2) return null;
        double n = ps.Count, sX = 0, sXX = 0, sU = 0, sXU = 0, sY = 0, sYY = 0, sV = 0, sYV = 0;
        foreach (var p in ps)
        {
            double x = p[0], y = p[1], u = p[2], v = p[3];
            sX += x; sXX += x * x; sU += u; sXU += x * u;
            sY += y; sYY += y * y; sV += v; sYV += y * v;
        }
        double dx = n * sXX - sX * sX, dy = n * sYY - sY * sY;
        if (System.Math.Abs(dx) < 1e-9 || System.Math.Abs(dy) < 1e-9) return null;
        double sx = (n * sXU - sX * sU) / dx, tx = (sU - sx * sX) / n;
        double sy = (n * sYV - sY * sV) / dy, ty = (sV - sy * sY) / n;
        return new[] { sx, 0, tx, 0, sy, ty, 0, 0 };
    }

    /// <summary>Robust fit: RANSAC over 2-point minimal samples (exhaustive for the small hand-captured
    /// sets) to find the LARGEST subset consistent with one <b>scale+translation</b> (the true model),
    /// rejecting mis-picked nodes (a capture that grabbed the wrong tile near the cursor — the dominant
    /// error), then refit on those inliers. The tight 4-DOF model is what makes rejection work: a 6-DOF
    /// affine has enough freedom (shear) to fit even a mis-pick into a 4-point "consensus", so it can't
    /// tell good picks from bad. Returns the solution (8-coeff form, shear+persp=0) + inlier indices.
    /// Null if no consensus of ≥ <paramref name="minInliers"/> within <paramref name="inlierPx"/>.</summary>
    public static (double[] sol, System.Collections.Generic.List<int> inliers)? RobustFit(
        System.Collections.Generic.List<double[]> ps, double inlierPx = 12, int minInliers = 4)
    {
        if (ps.Count < minInliers) return null;
        int n = ps.Count;
        var bestIn = new System.Collections.Generic.List<int>();
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var seed = new System.Collections.Generic.List<double[]> { ps[i], ps[j] };
                var m = FitScaleTranslate(seed);   // needs spread in both x and y; returns null otherwise
                if (m == null) continue;
                var inl = new System.Collections.Generic.List<int>();
                for (var t = 0; t < n; t++) if (Resid(m, ps[t]) <= inlierPx) inl.Add(t);
                if (inl.Count > bestIn.Count) bestIn = inl;
            }
        if (bestIn.Count < minInliers) return null;
        var inPts = new System.Collections.Generic.List<double[]>();
        foreach (var t in bestIn) inPts.Add(ps[t]);
        var refit = FitScaleTranslate(inPts);
        return refit == null ? null : (refit, bestIn);
    }

    /// <summary>Reprojection error (px) of one correspondence under solution s.</summary>
    public static double Resid(double[] s, double[] p)
    {
        double x = p[0], y = p[1], w = s[6] * x + s[7] * y + 1;
        if (System.Math.Abs(w) < 1e-9) return double.MaxValue;
        double su = (s[0] * x + s[1] * y + s[2]) / w, sv = (s[3] * x + s[4] * y + s[5]) / w;
        return System.Math.Sqrt((su - p[2]) * (su - p[2]) + (sv - p[3]) * (sv - p[3]));
    }

    /// <summary>Centroid + isotropic scale (mean distance → √2) over coordinate pair at column <paramref name="col"/>.</summary>
    private static (double scale, double cx, double cy) NormParams(System.Collections.Generic.List<double[]> ps, int col)
    {
        double cx = 0, cy = 0; foreach (var p in ps) { cx += p[col]; cy += p[col + 1]; }
        cx /= ps.Count; cy /= ps.Count;
        double md = 0; foreach (var p in ps) md += System.Math.Sqrt((p[col] - cx) * (p[col] - cx) + (p[col + 1] - cy) * (p[col + 1] - cy));
        md /= ps.Count;
        return md < 1e-9 ? (0, cx, cy) : (System.Math.Sqrt(2) / md, cx, cy);
    }

    /// <summary>3×3 (row-major) matrix product a·b.</summary>
    private static double[] Mul3(double[] a, double[] b)
    {
        var m = new double[9];
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) { double s = 0; for (var k = 0; k < 3; k++) s += a[r * 3 + k] * b[k * 3 + c]; m[r * 3 + c] = s; }
        return m;
    }

    /// <summary>Gaussian elimination with partial pivoting. Null if singular.</summary>
    private static double[]? SolveLinear(double[,] M, double[] b, int n)
    {
        var a = new double[n, n + 1];
        for (var i = 0; i < n; i++) { for (var j = 0; j < n; j++) a[i, j] = M[i, j]; a[i, n] = b[i]; }
        for (var col = 0; col < n; col++)
        {
            var piv = col; for (var r = col + 1; r < n; r++) if (System.Math.Abs(a[r, col]) > System.Math.Abs(a[piv, col])) piv = r;
            if (System.Math.Abs(a[piv, col]) < 1e-12) return null;
            if (piv != col) for (var j = 0; j <= n; j++) (a[col, j], a[piv, j]) = (a[piv, j], a[col, j]);
            for (var r = 0; r < n; r++) { if (r == col) continue; var f = a[r, col] / a[col, col]; for (var j = col; j <= n; j++) a[r, j] -= f * a[col, j]; }
        }
        var x = new double[n]; for (var i = 0; i < n; i++) x[i] = a[i, n] / a[i, i]; return x;
    }
}

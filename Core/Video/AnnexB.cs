namespace ScreenShareLan;

/// <summary>
/// Junta o stream Annex-B que sai do ffmpeg e emite 1 Access Unit (frame) por vez,
/// cortando nos AUD (NAL type 9, que o ffmpeg insere com aud=1 / h264_metadata=aud=insert).
/// Marca keyframe se a AU contiver IDR (type 5) ou SPS (type 7).
/// </summary>
internal sealed class AnnexBSplitter
{
    private byte[] _buf = new byte[1 << 16];
    private int _len;

    public void Append(byte[] data, int count, Action<byte[], bool> emit)
    {
        EnsureCapacity(_len + count);
        Buffer.BlockCopy(data, 0, _buf, _len, count);
        _len += count;

        // alinha o buffer no primeiro AUD (descarta lixo antes dele)
        int first = FindAud(_buf, 0, _len);
        if (first < 0)
        {
            if (_len > (1 << 22)) _len = 0; // trava de seguranca
            return;
        }
        if (first > 0) Shift(first);

        // enquanto achar o proximo AUD, a AU e tudo que esta entre os dois
        while (true)
        {
            int next = FindAud(_buf, 3, _len);
            if (next < 0) break;
            var au = new byte[next];
            Buffer.BlockCopy(_buf, 0, au, 0, next);
            emit(au, ContainsKeyFrame(au, next));
            Shift(next);
        }
    }

    private void Shift(int from)
    {
        int rem = _len - from;
        if (rem > 0) Buffer.BlockCopy(_buf, from, _buf, 0, rem);
        _len = rem;
    }

    private void EnsureCapacity(int need)
    {
        if (need <= _buf.Length) return;
        int cap = _buf.Length;
        while (cap < need) cap <<= 1;
        Array.Resize(ref _buf, cap);
    }

    // acha o inicio de um AUD (start code 00 00 01 + NAL type 9), a partir de 'start'
    private static int FindAud(byte[] b, int start, int len)
    {
        for (int i = Math.Max(0, start); i + 4 <= len; i++)
        {
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1)
            {
                int nalType = b[i + 3] & 0x1F;
                if (nalType == 9)
                    return (i > 0 && b[i - 1] == 0) ? i - 1 : i; // pega o 00 extra do start code de 4 bytes
            }
        }
        return -1;
    }

    private static bool ContainsKeyFrame(byte[] au, int len)
    {
        for (int i = 0; i + 4 <= len; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
            {
                int t = au[i + 3] & 0x1F;
                if (t == 5 || t == 7) return true; // IDR ou SPS
            }
        }
        return false;
    }
}

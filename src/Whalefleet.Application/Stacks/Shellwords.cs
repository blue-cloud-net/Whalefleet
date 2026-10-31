namespace Whalefleet.Application.Stacks;

/// <summary>
/// Shell 风格的单词拆分：把 <c>command: "nginx -g 'daemon off;'"</c> 拆成 argv。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：Compose 的 <c>command</c> 允许写成字符串，
/// 而字符串是交给 shell 解析的——引号必须生效，否则
/// <c>command: sh -c "echo hello world"</c> 会被拆成三个独立的词，
/// 参数边界整个错位。</para>
/// <para><b>只做 shell 的一小部分</b>：引号（单 / 双）与转义（反斜杠）。
/// 变量展开、命令替换、通配符这些<b>不做</b>——
/// 它们需要一套真实的 shell 环境，而本项目没有；而且 Compose 的变量插值
/// 已在解析阶段被明确拒绝（<c>1904</c>），这里再放行 shell 展开只会让语义更乱。</para>
/// <para><b>与真正的 shell 的差异（刻意为之）</b>：
/// 双引号内的 <c>$</c> 与反引号**不会**被展开，
/// 而单引号内的内容完全按字面处理（这与 POSIX 一致）。</para>
/// </remarks>
internal static class Shellwords
{
    /// <summary>
    /// 拆分命令串。
    /// </summary>
    /// <param name="text">原始命令串。</param>
    /// <returns>argv 列表；空白串返回空列表。</returns>
    /// <exception cref="Common.BusinessException">引号未闭合（<c>1904</c>）。</exception>
    public static List<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var hasWord = false;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (char.IsWhiteSpace(character))
            {
                if (hasWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    hasWord = false;
                }

                index++;
                continue;
            }

            switch (character)
            {
                case '\'':
                    index = ReadSingleQuoted(text, index, current, ref hasWord);
                    continue;

                case '"':
                    index = ReadDoubleQuoted(text, index, current, ref hasWord);
                    continue;

                case '\\' when index + 1 < text.Length:
                    current.Append(text[index + 1]);
                    hasWord = true;
                    index += 2;
                    continue;

                default:
                    current.Append(character);
                    hasWord = true;
                    index++;
                    continue;
            }
        }

        if (hasWord)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>
    /// 读取单引号段（内容按字面处理）。
    /// </summary>
    /// <param name="text">原始命令串。</param>
    /// <param name="start">起始引号位置。</param>
    /// <param name="current">当前单词缓冲。</param>
    /// <param name="hasWord">是否已有单词内容。</param>
    /// <returns>闭合引号之后的位置。</returns>
    private static int ReadSingleQuoted(
        string text,
        int start,
        System.Text.StringBuilder current,
        ref bool hasWord)
    {
        var index = start + 1;

        while (index < text.Length)
        {
            if (text[index] == '\'')
            {
                // 单引号内的内容即使为空也算「这个单词存在」：
                // `command: ""` 的语义是「空的命令」，不是「没有命令」。
                hasWord = true;
                return index + 1;
            }

            current.Append(text[index]);
            index++;
        }

        throw new Common.BusinessException(
            Common.ErrorCodes.ComposeInvalid,
            "Compose 文件里的 command 字符串有未闭合的单引号");
    }

    /// <summary>
    /// 读取双引号段（内容按字面处理，但反斜杠可转义引号）。
    /// </summary>
    /// <param name="text">原始命令串。</param>
    /// <param name="start">起始引号位置。</param>
    /// <param name="current">当前单词缓冲。</param>
    /// <param name="hasWord">是否已有单词内容。</param>
    /// <returns>闭合引号之后的位置。</returns>
    private static int ReadDoubleQuoted(
        string text,
        int start,
        System.Text.StringBuilder current,
        ref bool hasWord)
    {
        var index = start + 1;

        while (index < text.Length)
        {
            var character = text[index];

            if (character == '\\' && index + 1 < text.Length)
            {
                current.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (character == '"')
            {
                hasWord = true;
                return index + 1;
            }

            current.Append(character);
            index++;
        }

        throw new Common.BusinessException(
            Common.ErrorCodes.ComposeInvalid,
            "Compose 文件里的 command 字符串有未闭合的双引号");
    }
}

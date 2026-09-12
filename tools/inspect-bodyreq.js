// AuraCanAI 日志分析:检查发给 DeepSeek 的请求里 messages 长什么样(历史是否重复/注入了几条 system)。
//
// 用法(Git Bash / CMD 均可,需 Node):
//   node tools/inspect-bodyreq.js             # 看最后 3 条 BODYREQ 的 message 列表
//   node tools/inspect-bodyreq.js 10          # 看最后 10 条
//   node tools/inspect-bodyreq.js 3 <index>   # 额外打印最后一条里第 index 条 message 的完整内容
//
// 日志路径默认国服:C:\Users\<你>\AppData\Roaming\XIVLauncherCN\dalamud.log(BODYREQ 由 AuraCanAiCore.SendBodyChatAsync 输出)
// 背景:曾经出现「assistant 每条重复两遍」(自身回显没去重)与「一个请求里好几条注入 system」(场景/现场动作/补台词各一条),
// 这个脚本就是用来一眼看出这两类问题的。

const fs = require('fs');
const os = require('os');
const path = require('path');

const LOG = process.argv[4] || path.join(os.homedir(), 'AppData', 'Roaming', 'XIVLauncherCN', 'dalamud.log');
const n = parseInt(process.argv[2] || '3', 10);
const dumpIdx = process.argv[3] !== undefined ? parseInt(process.argv[3], 10) : -1;

// 从 "messages":[ 起按括号配平切出数组(自己处理字符串转义),避免整条 JSON 里别处的小毛病影响分析
function sliceArray(s, start) {
  let depth = 0, inStr = false, esc = false;
  for (let i = start; i < s.length; i++) {
    const c = s[i];
    if (inStr) {
      if (esc) esc = false;
      else if (c === '\\') esc = true;
      else if (c === '"') inStr = false;
      continue;
    }
    if (c === '"') { inStr = true; continue; }
    if (c === '[' || c === '{') depth++;
    else if (c === ']' || c === '}') { depth--; if (depth === 0) return s.slice(start, i + 1); }
  }
  return null;
}

const lines = fs.readFileSync(LOG, 'utf8').split(/\r?\n/);
const reqs = [];
for (const line of lines) {
  const i = line.indexOf('BODYREQ:');
  if (i < 0) continue;
  const raw = line.slice(i + 8);
  const mIdx = raw.indexOf('"messages":');
  let msgs = null;
  if (mIdx >= 0) {
    const arr = sliceArray(raw, raw.indexOf('[', mIdx));
    if (arr) { try { msgs = JSON.parse(arr); } catch (e) { msgs = 'ERR ' + e.message; } }
  }
  reqs.push({ ts: line.slice(0, 23), msgs, hasTools: raw.includes('"tools":') });
}

if (!reqs.length) { console.log('日志里没有 BODYREQ(路径: ' + LOG + ')'); process.exit(0); }
console.log('日志: ' + LOG);
console.log('BODYREQ 总数: ' + reqs.length + '(显示最后 ' + n + ' 条)');

const last = reqs[reqs.length - 1];
for (const r of reqs.slice(-n)) {
  console.log('\n============ ' + r.ts + '  tools=' + r.hasTools + ' ============');
  if (!Array.isArray(r.msgs)) { console.log('messages 解析失败: ' + r.msgs); continue; }
  const sysCount = r.msgs.filter(m => m.role === 'system').length;
  console.log('messages: ' + r.msgs.length + ' 其中 system=' + sysCount);
  const seen = {};
  r.msgs.forEach((m, idx) => {
    let tag = '';
    if (m.tool_calls) tag += ' [CALL ' + m.tool_calls.map(t => t.function.name + '(' + String(t.function.arguments).slice(0, 40) + ')').join(',') + ']';
    if (m.tool_call_id) tag += ' [RESULT]';
    const c = String(m.content == null ? '' : m.content);
    const firstLine = c.split('\n')[0];
    const preview = (firstLine.length > 150 ? firstLine.slice(0, 150) + '…' : firstLine)
      + (c.length > firstLine.length ? ' (换行+' + (c.length - firstLine.length) + '字)' : '');
    seen[m.role + '|' + c] = (seen[m.role + '|' + c] || 0) + 1;
    console.log(String(idx).padStart(3) + ' ' + m.role.padEnd(9) + tag + ' ' + JSON.stringify(preview));
  });
  const dups = Object.entries(seen).filter(([, v]) => v > 1);
  console.log(dups.length
    ? '!! 重复项: ' + dups.map(([k, v]) => 'x' + v + ' ' + k.slice(0, 24)).join(' ; ')
    : '无重复');
  if (sysCount > 2) console.log('!! 注入的 system 偏多(人设卡 1 条属正常,其余应合并成最后一条)');
  if (r !== last && dumpIdx >= 0) continue;
  if (r === last && dumpIdx >= 0 && Array.isArray(r.msgs) && r.msgs[dumpIdx]) {
    console.log('\n---- 最后一条请求里 message[' + dumpIdx + '] 全文 ----');
    console.log(r.msgs[dumpIdx].content);
  }
}

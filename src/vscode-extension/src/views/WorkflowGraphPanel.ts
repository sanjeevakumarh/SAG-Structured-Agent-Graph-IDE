import * as vscode from 'vscode';
import { WorkflowDefinition, WorkflowInstance, WorkflowStepDef } from '../client/MessageProtocol';

// Static registry: one panel per workflow instance
const openPanels = new Map<string, WorkflowGraphPanel>();

export class WorkflowGraphPanel {
    private readonly panel: vscode.WebviewPanel;
    private definition: WorkflowDefinition;
    private instance: WorkflowInstance;

    private constructor(
        context: vscode.ExtensionContext,
        instance: WorkflowInstance,
        definition: WorkflowDefinition
    ) {
        this.instance = instance;
        this.definition = definition;

        this.panel = vscode.window.createWebviewPanel(
            'sagIDE.workflowGraph',
            `Workflow: ${instance.definitionName}`,
            vscode.ViewColumn.Beside,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
            }
        );

        this.panel.webview.html = this.buildHtml();

        // Messages from the WebView
        this.panel.webview.onDidReceiveMessage(msg => {
            if (msg.command === 'cancel') {
                vscode.commands.executeCommand('sagIDE.cancelWorkflowInstance', msg.instanceId);
            } else if (msg.command === 'pause') {
                vscode.commands.executeCommand('sagIDE.pauseWorkflowInstance', msg.instanceId);
            } else if (msg.command === 'resume') {
                vscode.commands.executeCommand('sagIDE.resumeWorkflowInstance', msg.instanceId);
            } else if (msg.command === 'updateContext') {
                vscode.commands.executeCommand('sagIDE.updateWorkflowContext', msg.instanceId);
            } else if (msg.command === 'openTask') {
                vscode.commands.executeCommand('sagIDE.openTaskOutput', msg.taskId);
            } else if (msg.command === 'approveStep') {
                vscode.commands.executeCommand('sagIDE.approveWorkflowStep',
                    msg.instanceId, msg.stepId, true, undefined);
            } else if (msg.command === 'rejectStep') {
                vscode.commands.executeCommand('sagIDE.approveWorkflowStep',
                    msg.instanceId, msg.stepId, false, undefined);
            }
        }, undefined, context.subscriptions);

        this.panel.onDidDispose(() => {
            openPanels.delete(instance.instanceId);
        }, undefined, context.subscriptions);
    }

    // ── Public static API ────────────────────────────────────────────────────

    static show(
        context: vscode.ExtensionContext,
        instance: WorkflowInstance,
        definition: WorkflowDefinition
    ): void {
        const existing = openPanels.get(instance.instanceId);
        if (existing) {
            existing.panel.reveal(vscode.ViewColumn.Beside);
            existing.update(instance);
            return;
        }
        const panel = new WorkflowGraphPanel(context, instance, definition);
        openPanels.set(instance.instanceId, panel);
    }

    static update(instanceId: string, instance: WorkflowInstance): void {
        const existing = openPanels.get(instanceId);
        if (existing) {
            existing.update(instance);
        }
    }

    // ── Instance methods ─────────────────────────────────────────────────────

    private update(instance: WorkflowInstance): void {
        this.instance = instance;
        this.panel.webview.postMessage({ command: 'update', instance });
    }

    // ── HTML generation ──────────────────────────────────────────────────────

    private buildHtml(): string {
        const def = this.definition;
        const inst = this.instance;
        const layers = topologicalLayers(def.steps);
        const stepsJson = JSON.stringify(def.steps);
        const instanceJson = JSON.stringify(inst);

        return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<title>Workflow Graph</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size);
         background: var(--vscode-editor-background); color: var(--vscode-foreground);
         overflow-x: auto; padding: 0; }
  #toolbar { display: flex; align-items: center; gap: 12px; padding: 8px 16px;
             background: var(--vscode-sideBar-background); border-bottom: 1px solid var(--vscode-panel-border); }
  #toolbar h2 { font-size: 13px; font-weight: 600; flex: 1; }
  .status-badge { font-size: 11px; padding: 2px 8px; border-radius: 10px; font-weight: 500; }
  .status-running  { background: #1a3a5c; color: #5ba9e0; }
  .status-completed{ background: #1a3c1e; color: #5cb85c; }
  .status-failed   { background: #3c1a1a; color: #e05b5b; }
  .status-cancelled{ background: #3a3a3a; color: #888; }
  .status-paused   { background: #2a2a1a; color: #d4b04a; }
  #elapsed { font-size: 11px; color: var(--vscode-descriptionForeground); }
  button { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground);
           border: none; padding: 4px 10px; border-radius: 3px; cursor: pointer; font-size: 12px; }
  button:hover { background: var(--vscode-button-secondaryHoverBackground); }
  button.danger { background: #5c1a1a; color: #e05b5b; }
  button.danger:hover { background: #7a2020; }

  #graph-container { position: relative; padding: 20px; min-height: 300px; }
  .layer { display: flex; justify-content: center; gap: 20px; margin-bottom: 60px; position: relative; z-index: 1; }
  .step-card { width: 180px; border: 1px solid var(--vscode-panel-border); border-radius: 6px;
               padding: 10px 12px; cursor: pointer; transition: border-color 0.15s; }
  .step-card:hover { border-color: var(--vscode-focusBorder); }
  .step-card.status-pending            { border-color: #555; opacity: 0.7; }
  .step-card.status-running            { border-color: #5ba9e0; box-shadow: 0 0 8px rgba(91,169,224,0.3); }
  .step-card.status-completed          { border-color: #5cb85c; }
  .step-card.status-failed             { border-color: #e05b5b; }
  .step-card.status-skipped            { border-color: #555; opacity: 0.5; }
  .step-card.status-rejected           { border-color: #c05000; opacity: 0.7; }
  .step-card.status-waitingForApproval { border-color: #d4a017; box-shadow: 0 0 10px rgba(212,160,23,0.4); animation: pulse-border 2s ease-in-out infinite; }
  @keyframes pulse-border { 0%,100% { box-shadow: 0 0 6px rgba(212,160,23,0.3); } 50% { box-shadow: 0 0 14px rgba(212,160,23,0.7); } }
  .step-card.type-router          { transform: none; clip-path: polygon(50% 0%, 100% 50%, 50% 100%, 0% 50%);
                                    width: 80px; height: 80px; display: flex; align-items: center; justify-content: center; padding: 4px; }
  .step-card.type-tool            { border-style: dashed; }
  .step-card.type-constraint      { border-style: dotted; background: rgba(120,80,180,0.08); }
  .step-card.type-human_approval  { border-style: double; background: rgba(212,160,23,0.06); }
  .approval-btns { display: flex; gap: 6px; margin-top: 8px; }
  .btn-approve { background: #1a4a1e; color: #5cb85c; border: 1px solid #5cb85c; padding: 3px 10px; border-radius: 3px; cursor: pointer; font-size: 11px; }
  .btn-approve:hover { background: #2a6a2e; }
  .btn-reject  { background: #4a1a1a; color: #e05b5b; border: 1px solid #e05b5b; padding: 3px 10px; border-radius: 3px; cursor: pointer; font-size: 11px; }
  .btn-reject:hover  { background: #6a2a2a; }
  .step-header { display: flex; align-items: center; gap: 6px; margin-bottom: 4px; }
  .step-icon { font-size: 14px; }
  .step-label { font-size: 12px; font-weight: 600; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .step-iter { font-size: 10px; color: var(--vscode-descriptionForeground); }
  .step-meta { font-size: 10px; color: var(--vscode-descriptionForeground); }
  .step-issues { font-size: 10px; color: #e0a050; margin-top: 2px; }
  @keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
  .spin { display: inline-block; animation: spin 1s linear infinite; }

  svg#arrows { position: absolute; top: 0; left: 0; width: 100%; height: 100%; pointer-events: none; z-index: 0; overflow: visible; }
  svg#arrows line { stroke: var(--vscode-panel-border); stroke-width: 1.5; marker-end: url(#arrowhead); }
  svg#arrows line.active { stroke: #5ba9e0; }
</style>
</head>
<body>

<div id="toolbar">
  <h2 id="title">${escapeHtml(def.name)}</h2>
  <span id="status-badge" class="status-badge status-${inst.status}">${inst.status}</span>
  <span id="elapsed"></span>
  <button id="btn-pause"  style="display:none" onclick="pauseWorkflow()">⏸ Pause</button>
  <button id="btn-resume" style="display:none" onclick="resumeWorkflow()">▶ Resume</button>
  <button id="btn-ctx"    style="display:none" onclick="updateCtx()">✏ Edit Context</button>
  <button id="btn-cancel" class="danger" style="display:none" onclick="cancelWorkflow()">✕ Cancel</button>
</div>

<div id="graph-container">
  <svg id="arrows">
    <defs>
      <marker id="arrowhead" markerWidth="10" markerHeight="7" refX="10" refY="3.5" orient="auto">
        <polygon points="0 0, 10 3.5, 0 7" fill="var(--vscode-panel-border)"/>
      </marker>
    </defs>
  </svg>
  <div id="layers"></div>
</div>

<script>
  const vscode = acquireVsCodeApi();
  const allSteps = ${stepsJson};
  let currentInstance = ${instanceJson};
  const startTime = new Date(currentInstance.createdAt).getTime();

  // ── Render ───────────────────────────────────────────────────────────────

  function render() {
    const inst = currentInstance;
    // toolbar
    document.getElementById('status-badge').className = 'status-badge status-' + inst.status;
    document.getElementById('status-badge').textContent = inst.status;
    document.getElementById('btn-pause').style.display  = inst.status === 'running' ? '' : 'none';
    document.getElementById('btn-resume').style.display = inst.status === 'paused'  ? '' : 'none';
    document.getElementById('btn-ctx').style.display    = (inst.status === 'running' || inst.status === 'paused') ? '' : 'none';
    document.getElementById('btn-cancel').style.display = (inst.status === 'running' || inst.status === 'paused') ? '' : 'none';

    const layersEl = document.getElementById('layers');
    layersEl.innerHTML = '';
    const layers = ${JSON.stringify(layers)};

    layers.forEach(layer => {
      const rowEl = document.createElement('div');
      rowEl.className = 'layer';
      layer.forEach(stepId => {
        const stepDef = allSteps.find(s => s.id === stepId);
        if (!stepDef) return;
        const exec = inst.stepExecutions[stepId];
        const status = exec ? exec.status : 'pending';
        const card = buildCard(stepDef, exec, status);
        card.id = 'step-' + stepId;
        rowEl.appendChild(card);
      });
      layersEl.appendChild(rowEl);
    });

    requestAnimationFrame(drawArrows);
  }

  function buildCard(stepDef, exec, status) {
    const typeClass = ['router','tool','constraint','human_approval'].includes(stepDef.type)
      ? ' type-' + stepDef.type : '';
    const div = document.createElement('div');
    div.className = 'step-card status-' + status + typeClass;
    if (exec && exec.taskId && status !== 'waitingForApproval') {
      div.onclick = () => vscode.postMessage({ command: 'openTask', taskId: exec.taskId });
    }

    const icon   = stepTypeIcon(stepDef.type, status);
    const iter   = exec && exec.iteration > 1 ? 'it.' + exec.iteration : '';
    const issues = exec && exec.issueCount > 0 ? exec.issueCount + ' issues' : '';
    const exitBadge = exec && exec.exitCode != null
      ? '<span style="font-size:10px;color:' + (exec.exitCode === 0 ? '#5cb85c' : '#e05b5b') + '">exit ' + exec.exitCode + '</span>'
      : '';
    const meta = stepDef.type === 'tool'           ? (stepDef.command  || '').split(' ').slice(0,2).join(' ')
               : stepDef.type === 'constraint'     ? (stepDef.constraintExpr || '')
               : stepDef.type === 'router'         ? '◇ router'
               : stepDef.type === 'human_approval' ? '👤 approval gate'
               : stepDef.agent || (stepDef.modelId || stepDef.modelProvider || '');

    const approvalBtns = (status === 'waitingForApproval')
      ? \`<div class="approval-btns">
           <button class="btn-approve" onclick="event.stopPropagation(); approveStep('\${stepDef.id}')">✓ Approve</button>
           <button class="btn-reject"  onclick="event.stopPropagation(); rejectStep('\${stepDef.id}')">✗ Reject</button>
         </div>\`
      : '';

    div.innerHTML = \`
      <div class="step-header">
        <span class="step-icon">\${icon}</span>
        <span class="step-label" title="\${stepDef.id}">\${stepDef.id}</span>
        <span class="step-iter">\${iter}</span>
      </div>
      <div class="step-meta" title="\${meta}" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">\${meta}</div>
      \${exitBadge}
      \${issues ? '<div class="step-issues">⚠ ' + issues + '</div>' : ''}
      \${approvalBtns}
    \`;
    return div;
  }

  function stepTypeIcon(type, status) {
    if (status === 'running')            return '<span class="spin">⟳</span>';
    if (status === 'waitingForApproval') return '🕐';
    if (status === 'completed')          return type === 'tool' ? '⚙' : type === 'constraint' ? '✔' : type === 'human_approval' ? '✓' : '✓';
    if (status === 'failed')             return '✗';
    if (status === 'rejected')           return '⊘';
    if (status === 'skipped')            return '—';
    // pending
    return type === 'tool' ? '⚙' : type === 'constraint' ? '?' : type === 'human_approval' ? '👤' : '○';
  }

  function approveStep(stepId) {
    vscode.postMessage({ command: 'approveStep', instanceId: currentInstance.instanceId, stepId });
  }
  function rejectStep(stepId) {
    vscode.postMessage({ command: 'rejectStep', instanceId: currentInstance.instanceId, stepId });
  }


  function drawArrows() {
    const svg = document.getElementById('arrows');
    // Remove old lines but keep defs
    Array.from(svg.querySelectorAll('line')).forEach(l => l.remove());

    const container = document.getElementById('graph-container');
    const containerRect = container.getBoundingClientRect();

    allSteps.forEach(step => {
      if (!step.dependsOn || step.dependsOn.length === 0) return;
      const toEl = document.getElementById('step-' + step.id);
      if (!toEl) return;
      const toRect = toEl.getBoundingClientRect();
      const toX = toRect.left - containerRect.left + toRect.width / 2;
      const toY = toRect.top - containerRect.top;

      step.dependsOn.forEach(depId => {
        const fromEl = document.getElementById('step-' + depId);
        if (!fromEl) return;
        const fromRect = fromEl.getBoundingClientRect();
        const fromX = fromRect.left - containerRect.left + fromRect.width / 2;
        const fromY = fromRect.top - containerRect.top + fromRect.height;

        const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
        line.setAttribute('x1', fromX);
        line.setAttribute('y1', fromY);
        line.setAttribute('x2', toX);
        line.setAttribute('y2', toY);
        const exec = currentInstance.stepExecutions[step.id];
        if (exec && (exec.status === 'running' || exec.status === 'completed')) {
          line.classList.add('active');
        }
        svg.appendChild(line);
      });
    });
  }

  // ── Elapsed timer ────────────────────────────────────────────────────────

  function updateElapsed() {
    const seconds = Math.floor((Date.now() - startTime) / 1000);
    const m = Math.floor(seconds / 60);
    const s = seconds % 60;
    document.getElementById('elapsed').textContent = m + ':' + String(s).padStart(2, '0');
  }
  setInterval(updateElapsed, 1000);
  updateElapsed();

  // ── Workflow controls ─────────────────────────────────────────────────────

  function cancelWorkflow() {
    vscode.postMessage({ command: 'cancel',        instanceId: currentInstance.instanceId });
  }
  function pauseWorkflow() {
    vscode.postMessage({ command: 'pause',         instanceId: currentInstance.instanceId });
  }
  function resumeWorkflow() {
    vscode.postMessage({ command: 'resume',        instanceId: currentInstance.instanceId });
  }
  function updateCtx() {
    vscode.postMessage({ command: 'updateContext', instanceId: currentInstance.instanceId });
  }

  // ── Messages from host ───────────────────────────────────────────────────

  window.addEventListener('message', event => {
    const msg = event.data;
    if (msg.command === 'update') {
      currentInstance = msg.instance;
      render();
    }
  });

  window.addEventListener('resize', drawArrows);

  render();
</script>
</body>
</html>`;
    }
}

// ─── Topological sort → layers ─────────────────────────────────────────────

function topologicalLayers(steps: WorkflowStepDef[]): string[][] {
    const depMap = new Map<string, string[]>();
    for (const s of steps) {
        depMap.set(s.id, s.dependsOn ?? []);
    }

    const layers: string[][] = [];
    const placed = new Set<string>();

    while (placed.size < steps.length) {
        const layer = steps
            .filter(s => !placed.has(s.id))
            .filter(s => (depMap.get(s.id) ?? []).every(dep => placed.has(dep)))
            .map(s => s.id);

        if (layer.length === 0) {
            // Cycle or unresolvable — place remaining in one layer
            steps.filter(s => !placed.has(s.id)).forEach(s => layer.push(s.id));
        }

        layer.forEach(id => placed.add(id));
        layers.push(layer);

        if (layer.length === 0) { break; }
    }

    return layers;
}

function escapeHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

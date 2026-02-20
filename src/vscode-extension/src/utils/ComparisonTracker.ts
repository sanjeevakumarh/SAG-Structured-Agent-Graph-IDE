import { TaskStatusResponse } from '../client/MessageProtocol';

interface ComparisonGroup {
    expectedCount: number;
    agentType: string;
    description: string;
    completedTasks: TaskStatusResponse[];
}

export class ComparisonTracker {
    private groups = new Map<string, ComparisonGroup>();

    registerGroup(groupId: string, count: number, agentType: string, description: string): void {
        this.groups.set(groupId, {
            expectedCount: count,
            agentType,
            description,
            completedTasks: [],
        });
    }

    /**
     * Call on every task update.
     * Returns the completed group when all tasks in the group have reached a terminal state
     * (Completed, Failed, or Cancelled). Returns null otherwise.
     */
    onTaskUpdate(status: TaskStatusResponse): ComparisonGroup | null {
        const groupId = status.comparisonGroupId;
        if (!groupId) { return null; }

        const group = this.groups.get(groupId);
        if (!group) { return null; }

        const terminal = status.status === 'Completed'
            || status.status === 'Failed'
            || status.status === 'Cancelled';
        if (!terminal) { return null; }

        // Avoid duplicates
        if (!group.completedTasks.find(t => t.taskId === status.taskId)) {
            group.completedTasks.push(status);
        }

        if (group.completedTasks.length >= group.expectedCount) {
            this.groups.delete(groupId);
            return group;
        }
        return null;
    }
}

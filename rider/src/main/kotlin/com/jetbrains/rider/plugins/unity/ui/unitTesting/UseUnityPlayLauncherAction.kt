package com.jetbrains.rider.plugins.unity.ui.unitTesting

import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.project.DumbAwareAction
import com.jetbrains.rider.model.UnitTestLaunchPreference
import com.jetbrains.rider.model.rdUnityModel
import com.jetbrains.rider.plugins.unity.isConnectedToEditor
import com.jetbrains.rider.projectView.solution

class UseUnityPlayLauncherAction : DumbAwareAction(PlayModeDescription, "Run with Unity Editor in Play Mode", null) {
    companion object {
        const val PlayModeDescription = "Unity Editor - Play Mode"
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        project.solution.rdUnityModel.unitTestPreference.value = UnitTestLaunchPreference.PlayMode
    }

    override fun update(e: AnActionEvent) {
        e.presentation.isVisible = true
    }
}
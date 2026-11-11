---
name: skill-creator
description: Creating a new skill or changing an existing one. Use it when the user wants to teach the assistant a procedure ("learn to…", "next time do it like this", "make a skill of it"), or when a task repeats in the conversation that would be worth writing down.
---

# Creating skills

A skill is a named procedure. The SKILLS list shows only its name and description; you load the whole procedure with `load_skill` when you need it. You save a skill with `save_skill`.

## 1. Find out what the skill should do

Before you write anything, agree with the user on:

- **Purpose:** what task the skill solves and how you recognize you should use it.
- **Procedure:** the steps, in what order, and what the result is (a file, an answer).
- **Inputs:** what the user must give, and what you look up yourself (files, memory).
- **Rules and exceptions:** what must not happen, when to ask, how to handle missing data.
- **Example:** one concrete case from start to end, if the user has one.

When the procedure has already happened in the conversation, start from it and ask only about what's missing. Ask one topic at a time.

Before you start, check the SKILLS list for a similar skill. If there is one, load it with `load_skill` and propose a change instead of a new skill.

## 2. Write it

- **name:** short, to the point, lowercase without diacritics, words joined by dashes (`issue-invoice`, `price-offer`).
- **description:** one or two sentences: what the skill does **and when to use it**. Skills are picked by their description, so use the words users use for the task.
- **instructions:** the procedure in Markdown:
  - a heading and one sentence on the purpose,
  - numbered steps in the imperative ("Find…", "Create…"), each with the concrete tool, file or template it works with,
  - rules and exceptions as bullets,
  - a short example of input and output, if useful.

Write briefly and concretely. Don't repeat general things the assistant knows; write down what is special for this project and this task. Never put passwords or keys into a skill.

## 3. Show it and save it

1. Show the user the name, description and procedure and ask whether it's right.
2. Once they agree, save it with `save_skill`. Admins save skills for the whole project; others save their own (these apply only to them and replace the project's skill of the same name for them). Tell a user who isn't an admin.
3. Confirm it's saved and that it's in the SKILLS list from the next message.

Changing an existing skill is the same: load it, propose the changes, and after the user agrees save it under the same name.

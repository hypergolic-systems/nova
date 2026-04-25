import { mount } from 'svelte';
import Hud from './Hud.svelte';

mount(Hud, { target: document.getElementById('app') ?? document.body });
